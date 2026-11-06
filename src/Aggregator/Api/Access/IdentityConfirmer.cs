using System.Text.Json;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// Turns a fresh password or passkey of the signed-in user into a short-lived confirmation (ADR-0013). Failed attempts
/// count towards the lockout and are audited, like failed sign-ins. Needs a session: an API token cannot confirm.
/// </summary>
internal sealed class IdentityConfirmer(
    SignInManager<AccountUser> signInManager,
    CurrentActor currentActor,
    IdentityConfirmations confirmations,
    IAuditTrail audit)
{
    private UserManager<AccountUser> Accounts => signInManager.UserManager;

    /// <returns>The confirmation; null when the password is wrong or the account is locked out.</returns>
    public async Task<IssuedConfirmation?> WithPasswordAsync(string password, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        var account = await Accounts.FindByIdAsync(user.Id.Value.ToString());
        if (account is null || !(await signInManager.CheckPasswordSignInAsync(account, password, lockoutOnFailure: true)).Succeeded)
        {
            await RecordAsync(user, AuditOutcome.Failed, "Password. Check failed.", cancellationToken);
            return null;
        }

        await RecordAsync(user, AuditOutcome.Succeeded, "Password", cancellationToken);
        return confirmations.Issue(user.Id);
    }

    /// <summary>Options for <c>navigator.credentials.get()</c>, limited to the signed-in user's passkeys.</summary>
    /// <returns>Null when the user has no account to confirm with.</returns>
    public async Task<string?> MakePasskeyOptionsAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        var account = await Accounts.FindByIdAsync(user.Id.Value.ToString());
        return account is null ? null : await signInManager.MakePasskeyRequestOptionsAsync(account);
    }

    /// <returns>The confirmation; null for a failed check, or a passkey of someone else.</returns>
    public async Task<IssuedConfirmation?> WithPasskeyAsync(string credentialJson, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        PasskeyAssertionResult<AccountUser> assertion;
        try
        {
            assertion = await signInManager.PerformPasskeyAssertionAsync(credentialJson);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
        {
            await RecordAsync(user, AuditOutcome.Failed, "Passkey. No valid answer.", cancellationToken);
            return null;
        }

        if (!assertion.Succeeded
            || UserId.From(assertion.User.Id) != user.Id
            || await Accounts.IsLockedOutAsync(assertion.User)
            || !(await Accounts.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey)).Succeeded)
        {
            await RecordAsync(user, AuditOutcome.Failed, "Passkey. Check failed.", cancellationToken);
            return null;
        }

        await RecordAsync(user, AuditOutcome.Succeeded, "Passkey", cancellationToken);
        return confirmations.Issue(user.Id);
    }

    private Task RecordAsync(User user, AuditOutcome outcome, string details, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditAction.IdentityConfirmed, outcome, new UserAuditTarget(user.Id, user.UserName), details, cancellationToken);
}
