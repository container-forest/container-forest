using System.Globalization;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>How a code finished a sign-in.</summary>
internal enum SecondFactor
{
    AuthenticatorApp,
    RecoveryCode,
}

/// <summary>
/// The second step of a password sign-in for an account with two-factor authentication (ADR-0019), and of a Discord
/// sign-in when it counts as a password. The first step leaves its result in the <c>cf_2fa</c> cookie: the user, their
/// security stamp, <i>Remember me</i> and the first factor, protected with Data Protection, for <see cref="Lifetime"/>. Identity's own two-factor cookie is not used: it also carries the
/// challenge of a passkey sign-in (ADR-0009), and the sign-in screen asks for one as soon as it opens.
/// </summary>
internal sealed class TwoFactorSignIn(
    SignInManager<AccountUser> signInManager,
    IUserRepository users,
    ISecretProtector protector,
    IHttpContextAccessor httpContext,
    IHostEnvironment environment,
    IAuditTrail audit,
    TimeProvider time)
{
    public const string CookieName = "cf_2fa";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private UserManager<AccountUser> Accounts => signInManager.UserManager;

    private HttpContext Http => httpContext.HttpContext ?? throw new InvalidOperationException("No request.");

    /// <summary>Remembers that the first factor of <paramref name="account"/> was right; the code comes next.</summary>
    /// <param name="firstFactor"><see cref="SignInMethods.Password"/> or <see cref="SignInMethods.Discord"/>, for the audit log.</param>
    public async Task BeginAsync(AccountUser account, bool rememberMe, string firstFactor)
    {
        var stamp = await Accounts.GetSecurityStampAsync(account);
        var expires = time.GetUtcNow() + Lifetime;
        var payload = string.Create(
            CultureInfo.InvariantCulture, $"{account.Id}|{stamp}|{(rememberMe ? 1 : 0)}|{expires.ToUnixTimeSeconds()}|{firstFactor}");
        Http.Response.Cookies.Append(CookieName, protector.Protect(SecretPurpose.TwoFactorSignIn, payload), CookieOptions(expires));
    }

    /// <summary>
    /// Checks the code of an authenticator app, or a recovery code, against the account whose password was just right,
    /// and starts the session. A wrong code counts towards the lockout, like a wrong password.
    /// </summary>
    /// <returns>
    /// The signed-in user; null without a pending sign-in (none, expired, or the account changed since), for a wrong
    /// code, a lockout or a deactivated user. Callers answer all of these the same way.
    /// </returns>
    public async Task<User?> CompleteAsync(string code, CancellationToken cancellationToken)
    {
        if (await PendingAsync() is not var (account, rememberMe, firstFactor))
        {
            return null;
        }

        var user = await users.FindAsync(UserId.From(account.Id), cancellationToken);
        if (user is not { IsActive: true } || await Accounts.IsLockedOutAsync(account))
        {
            await RecordFailureAsync(account, user, user is { IsActive: true } ? "Locked out." : "User is deactivated.", cancellationToken);
            return null;
        }

        var factor = await CheckAsync(account, code);
        if (factor is null)
        {
            await Accounts.AccessFailedAsync(account);
            await RecordFailureAsync(account, user, "Wrong code.", cancellationToken);
            return null;
        }

        Http.Response.Cookies.Delete(CookieName, CookieOptions(time.GetUtcNow()));
        await Accounts.ResetAccessFailedCountAsync(account);
        await signInManager.SignInAsync(account, isPersistent: rememberMe, authenticationMethod: SignInMethods.TwoFactor);
        var first = firstFactor == SignInMethods.Discord ? "Discord" : "Password";
        var details = factor == SecondFactor.RecoveryCode
            ? $"{first} and a recovery code; {await Accounts.CountRecoveryCodesAsync(account)} left"
            : $"{first} and an authenticator app";
        await PasswordSignIn.RecordSignInAsync(audit, user, details, cancellationToken);
        return user;
    }

    /// <summary>Six digits are a code of the app; anything else is tried as a recovery code, which works only once.</summary>
    private async Task<SecondFactor?> CheckAsync(AccountUser account, string code)
    {
        if (TotpAuthenticator.TryParseCode(code, out var totp))
        {
            return await Accounts.VerifyTwoFactorTokenAsync(account, Accounts.Options.Tokens.AuthenticatorTokenProvider, totp)
                ? SecondFactor.AuthenticatorApp
                : null;
        }

        var recovery = code.Trim();
        return recovery.Length is > 0 and <= 64 && (await Accounts.RedeemTwoFactorRecoveryCodeAsync(account, recovery)).Succeeded
            ? SecondFactor.RecoveryCode
            : null;
    }

    private async Task<(AccountUser Account, bool RememberMe, string FirstFactor)?> PendingAsync()
    {
        if (!Http.Request.Cookies.TryGetValue(CookieName, out var cookie) || string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        string payload;
        try
        {
            payload = protector.Unprotect(SecretPurpose.TwoFactorSignIn, cookie);
        }
        catch (SecretUnreadableException)
        {
            return null;
        }

        var parts = payload.Split('|');
        if (parts.Length != 5
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || time.GetUtcNow().ToUnixTimeSeconds() >= expires
            || await Accounts.FindByIdAsync(parts[0]) is not { TwoFactorEnabled: true } account
            || await Accounts.GetSecurityStampAsync(account) != parts[1])
        {
            // A changed security stamp: the password was changed or two-factor turned off since the password step.
            return null;
        }

        return (account, parts[2] == "1", parts[4]);
    }

    private CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = !environment.IsDevelopment() || Http.Request.IsHttps,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };

    private Task RecordFailureAsync(AccountUser account, User? user, string reason, CancellationToken cancellationToken)
    {
        var userId = UserId.From(account.Id);
        var name = user?.UserName ?? account.UserName ?? userId.ToString();
        return audit.RecordAsync(
            AuditAction.SignInFailed, AuditOutcome.Failed, new UserAuditTarget(userId, name), $"Two-factor code. {reason}",
            cancellationToken, new AuditPerson(userId, name, AuditChannel.Anonymous));
    }
}
