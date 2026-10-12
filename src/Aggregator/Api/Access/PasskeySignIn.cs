using System.Text.Json;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// Signing in without a password, with a passkey (WebAuthn) of ASP.NET Core Identity (ADR-0009). The browser asks
/// for options first; the challenge stays in a short-lived cookie of Identity until the browser answers.
/// </summary>
internal sealed class PasskeySignIn(SignInManager<AccountUser> signInManager, IUserRepository users, IAuditTrail audit)
{
    /// <summary>Options for <c>navigator.credentials.get()</c>, for any user: the browser offers the passkeys it has.</summary>
    public Task<string> MakeRequestOptionsAsync() => signInManager.MakePasskeyRequestOptionsAsync(null);

    /// <summary>
    /// Checks the browser's answer and starts a session, as a password sign-in does.
    /// </summary>
    /// <param name="credentialJson">The result of <c>navigator.credentials.get()</c>, serialized with <c>toJSON()</c>.</param>
    /// <returns>
    /// The signed-in user, or null for an unknown passkey, a failed check, a locked-out or deactivated user, or
    /// options that were never asked for. Callers answer all of these the same way.
    /// </returns>
    public async Task<User?> SignInAsync(string credentialJson, bool rememberMe, CancellationToken cancellationToken)
    {
        PasskeyAssertionResult<AccountUser> assertion;
        try
        {
            assertion = await signInManager.PerformPasskeyAssertionAsync(credentialJson);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
        {
            // No options were asked for in this browser (or they expired), or the answer is not a credential.
            return null;
        }

        if (!assertion.Succeeded || await signInManager.UserManager.IsLockedOutAsync(assertion.User))
        {
            // Without a known passkey there is no user to name; a failed check of a known one names its owner.
            if (assertion.User is { } owner)
            {
                await RecordFailureAsync(UserId.From(owner.Id), owner.UserName, assertion.Succeeded ? "Locked out." : "Check failed.", cancellationToken);
            }

            return null;
        }

        var user = await users.FindAsync(UserId.From(assertion.User.Id), cancellationToken);
        if (user is not { IsActive: true })
        {
            await RecordFailureAsync(UserId.From(assertion.User.Id), assertion.User.UserName, "User is deactivated.", cancellationToken);
            return null;
        }

        // Keeps the signature counter, which lets a cloned authenticator be noticed.
        var updated = await signInManager.UserManager.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
        if (!updated.Succeeded)
        {
            return null;
        }

        await signInManager.SignInAsync(assertion.User, isPersistent: rememberMe, authenticationMethod: "passkey");
        await PasswordSignIn.RecordSignInAsync(audit, user, "Passkey", cancellationToken);
        return user;
    }

    private Task RecordFailureAsync(UserId userId, string? userName, string reason, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            AuditAction.SignInFailed, AuditOutcome.Failed, new UserAuditTarget(userId, userName ?? userId.ToString()),
            $"Passkey. {reason}", cancellationToken, new AuditPerson(userId, userName, AuditChannel.Anonymous));
}
