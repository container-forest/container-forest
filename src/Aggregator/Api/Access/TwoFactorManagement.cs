using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <param name="Enabled">The password sign-in asks for a code of the authenticator app.</param>
/// <param name="RecoveryCodesLeft">Unused recovery codes; 0 when two-factor is off.</param>
/// <param name="Required">The system requires it of this user (an administrator without a passkey), so it cannot be turned off.</param>
internal sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft, bool Required);

/// <param name="SharedKey">The key in base32, for typing into the app; shown only while setting up.</param>
/// <param name="Uri">The otpauth:// link the QR code carries.</param>
internal sealed record TwoFactorSetup(string SharedKey, string Uri);

/// <summary>
/// Two-factor authentication with an authenticator app (TOTP, ADR-0019) for the signed-in user, only from a session
/// (like passkeys: a token must not change how the account signs in), and its reset by an administrator for a lost phone.
/// Turning it on, off, or making new recovery codes needs a fresh identity confirmation (ADR-0013).
/// </summary>
internal sealed class TwoFactorManagement(
    SignInManager<AccountUser> signInManager,
    CurrentActor currentActor,
    IUserRepository users,
    IdentityConfirmations confirmations,
    SignInSettingsService settings,
    ISystemNotifier notifier,
    IHttpContextAccessor httpContext,
    IAuditTrail audit)
{
    public const string Issuer = "container-forest";

    public const int RecoveryCodeCount = 10;

    private UserManager<AccountUser> Accounts => signInManager.UserManager;

    public async Task<TwoFactorStatus> GetMineAsync(CancellationToken cancellationToken)
    {
        var (user, account) = await SignedInAsync(cancellationToken);
        return await StatusAsync(user, account, cancellationToken);
    }

    /// <summary>
    /// A new key for the app, not yet in use: a code from the app turns it on (<see cref="EnableAsync"/>). Starting again
    /// replaces the key; with two-factor already on, turn it off first.
    /// </summary>
    /// <exception cref="DomainConflictException">Two-factor is already on.</exception>
    public async Task<TwoFactorSetup> BeginSetupAsync(string? confirmation, CancellationToken cancellationToken)
    {
        var (user, account) = await SignedInAsync(cancellationToken);
        confirmations.Require(confirmation, user.Id);
        if (account.TwoFactorEnabled)
        {
            throw new DomainConflictException("Two-factor authentication is already on; turn it off to set up another app.");
        }

        // Changes the security stamp: the user's other sessions end, this one is signed in again.
        Ensure(await Accounts.ResetAuthenticatorKeyAsync(account));
        await signInManager.RefreshSignInAsync(account);
        var key = await Accounts.GetAuthenticatorKeyAsync(account) ?? throw new InvalidOperationException("Identity made no authenticator key.");
        var host = httpContext.HttpContext?.Request.Host.Host;
        return new TwoFactorSetup(key, TotpAuthenticator.Uri(Issuer, host is null ? user.UserName : $"{user.UserName}@{host}", key));
    }

    /// <summary>Turns two-factor on with a code from the app set up with <see cref="BeginSetupAsync"/>.</summary>
    /// <returns>The recovery codes, shown only now.</returns>
    /// <exception cref="DomainValidationException">The code is wrong, or setup was not started.</exception>
    public Task<IReadOnlyList<string>> EnableAsync(string code, CancellationToken cancellationToken) =>
        audit.RunAsync(new AuditDraft(AuditAction.TwoFactorEnabled), async () =>
        {
            var (_, account) = await SignedInAsync(cancellationToken);
            if (account.TwoFactorEnabled)
            {
                throw new DomainConflictException("Two-factor authentication is already on.");
            }

            if (!TotpAuthenticator.TryParseCode(code, out var totp)
                || !await Accounts.VerifyTwoFactorTokenAsync(account, Accounts.Options.Tokens.AuthenticatorTokenProvider, totp))
            {
                throw new DomainValidationException("The code is not right: check the time on the phone and enter the current code.");
            }

            Ensure(await Accounts.SetTwoFactorEnabledAsync(account, true));
            var codes = await NewRecoveryCodesAsync(account);
            await signInManager.RefreshSignInAsync(account);
            return codes;
        }, cancellationToken);

    /// <summary>New recovery codes; the old ones stop working.</summary>
    /// <exception cref="DomainConflictException">Two-factor is off.</exception>
    public Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(string? confirmation, CancellationToken cancellationToken) =>
        audit.RunAsync(new AuditDraft(AuditAction.RecoveryCodesGenerated), async () =>
        {
            var (user, account) = await SignedInAsync(cancellationToken);
            confirmations.Require(confirmation, user.Id);
            return account.TwoFactorEnabled
                ? await NewRecoveryCodesAsync(account)
                : throw new DomainConflictException("Two-factor authentication is off.");
        }, cancellationToken);

    /// <summary>Turns two-factor off and forgets the key and the recovery codes.</summary>
    /// <exception cref="DomainConflictException">The system requires it of this user.</exception>
    public Task DisableMineAsync(string? confirmation, CancellationToken cancellationToken) =>
        audit.RunAsync(new AuditDraft(AuditAction.TwoFactorDisabled), async () =>
        {
            var (user, account) = await SignedInAsync(cancellationToken);
            confirmations.Require(confirmation, user.Id);
            if (await IsRequiredAsync(user, account, cancellationToken))
            {
                throw new DomainConflictException(
                    "Administrators must use an authenticator app with their password; add a passkey first to turn it off.");
            }

            await DisableAsync(account);
            await signInManager.RefreshSignInAsync(account);
        }, cancellationToken);

    /// <summary>For an administrator: turns another user's two-factor off, e.g. after a lost phone. Their sessions end.</summary>
    /// <exception cref="NotFoundException">No such user.</exception>
    public Task ResetForUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.TwoFactorReset);
        return audit.RunAsync(draft, async () =>
        {
            var actor = await currentActor.GetAdministratorAsync(cancellationToken);
            var user = await users.FindAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);
            draft.Target = new UserAuditTarget(user.Id, user.UserName);
            var account = await Accounts.FindByIdAsync(userId.Value.ToString()) ?? throw new NotFoundException("Account", userId);
            if (!account.TwoFactorEnabled)
            {
                draft.Details = "Was off";
                return;
            }

            await DisableAsync(account);
            await notifier.NotifyAsync(
                NotificationEventKinds.AccountSecurityChanged,
                $"Two-factor authentication of {user.UserName} was turned off by {actor.User.UserName}");
        }, cancellationToken);
    }

    /// <summary>Whether the user has two-factor on; for administrators' user lists.</summary>
    public async Task<bool> IsEnabledAsync(UserId userId) =>
        await Accounts.FindByIdAsync(userId.Value.ToString()) is { TwoFactorEnabled: true };

    /// <summary>
    /// True when the system requires two-factor of administrators and this administrator's session was signed in with
    /// one factor: a password alone or, when Discord counts as a password, Discord without the code. Such a session may
    /// only set up the app (<see cref="TwoFactorEnrollment"/>). A passkey sign-in is two factors; so is a password with
    /// the code.
    /// </summary>
    /// <param name="method">How the session was signed in (<see cref="SignInMethods"/>); null for a request without a session.</param>
    public async Task<bool> RequiresSetupAsync(User user, string? method, CancellationToken cancellationToken)
    {
        if (method is null or SignInMethods.TwoFactor or SignInMethods.Passkey || !user.IsAdmin)
        {
            return false;
        }

        var current = await settings.CurrentAsync(cancellationToken);
        if (!current.RequireTwoFactorForAdministrators || (method == SignInMethods.Discord && !current.RequireTwoFactorAfterDiscord))
        {
            return false;
        }

        // Set up during this session: the code was given then.
        var account = await Accounts.FindByIdAsync(user.Id.Value.ToString());
        if (account is null || account.TwoFactorEnabled)
        {
            return false;
        }

        // A session from before the method was kept: as before, a passkey on the account is enough.
        return method != SignInMethods.Unknown || (await Accounts.GetPasskeysAsync(account)).Count == 0;
    }

    private async Task<TwoFactorStatus> StatusAsync(User user, AccountUser account, CancellationToken cancellationToken) => new(
        account.TwoFactorEnabled,
        account.TwoFactorEnabled ? await Accounts.CountRecoveryCodesAsync(account) : 0,
        await IsRequiredAsync(user, account, cancellationToken));

    /// <summary>
    /// An administrator under the requirement who has no passkey: without the app they could only sign in to a limited
    /// session, so the app cannot be turned off. With a passkey they can, and then sign in with the passkey.
    /// </summary>
    private async Task<bool> IsRequiredAsync(User user, AccountUser account, CancellationToken cancellationToken) =>
        user.IsAdmin
        && (await settings.CurrentAsync(cancellationToken)).RequireTwoFactorForAdministrators
        && (await Accounts.GetPasskeysAsync(account)).Count == 0;

    private async Task<IReadOnlyList<string>> NewRecoveryCodesAsync(AccountUser account) =>
        [.. await Accounts.GenerateNewTwoFactorRecoveryCodesAsync(account, RecoveryCodeCount) ?? []];

    /// <summary>Changes the security stamp, so every session of the account ends.</summary>
    private async Task DisableAsync(AccountUser account)
    {
        Ensure(await Accounts.SetTwoFactorEnabledAsync(account, false));
        Ensure(await Accounts.RemoveAuthenticationTokenAsync(account, ProtectedAccountStore.IdentityProvider, "AuthenticatorKey"));
        Ensure(await Accounts.RemoveAuthenticationTokenAsync(account, ProtectedAccountStore.IdentityProvider, "RecoveryCodes"));
        Ensure(await Accounts.RemoveAuthenticationTokenAsync(account, TotpAuthenticator.LoginProvider, TotpAuthenticator.LastStepName));
    }

    private async Task<(User User, AccountUser Account)> SignedInAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        var account = await Accounts.FindByIdAsync(user.Id.Value.ToString()) ?? throw new NotFoundException("Account", user.Id);
        return (user, account);
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Identity refused a two-factor change: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }
    }
}
