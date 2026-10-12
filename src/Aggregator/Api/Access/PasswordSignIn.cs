using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// Signs users in and out with a user name and password, and changes the signed-in user's password. Every attempt is
/// recorded in the audit log, failed sign-ins with the name they gave.
/// </summary>
internal sealed class PasswordSignIn(
    SignInManager<AccountUser> signInManager,
    IUserRepository users,
    CurrentActor currentActor,
    ICurrentUser currentUser,
    IAuditTrail audit)
{
    private const string IncorrectPassword = "Incorrect password.";

    /// <summary>Stands in for an unknown user name, so it costs a password hash like a known one.</summary>
    private static readonly AccountUser DecoyAccount = new(User.Create("decoy", DateTimeOffset.UnixEpoch));

    private static string? decoyHash;

    private UserManager<AccountUser> Accounts => signInManager.UserManager;

    /// <summary>
    /// Checks the password and starts a session: a persistent cookie with <paramref name="rememberMe"/>, a
    /// session cookie otherwise. Failed checks count towards the lockout.
    /// </summary>
    /// <returns>
    /// The signed-in user, or null when the name is unknown, the password is wrong, the account is locked out or
    /// the user is deactivated. Callers answer all of these the same way, so a response does not reveal which.
    /// </returns>
    public async Task<User?> SignInAsync(string userName, string password, bool rememberMe, CancellationToken cancellationToken)
    {
        var account = await Accounts.FindByNameAsync(userName);
        if (account is null)
        {
            // Spend the time of a password check anyway: a faster answer would tell which user names exist.
            decoyHash ??= Accounts.PasswordHasher.HashPassword(DecoyAccount, Guid.NewGuid().ToString());
            Accounts.PasswordHasher.VerifyHashedPassword(DecoyAccount, decoyHash, password);
            await RecordFailureAsync(null, userName, "Unknown user name.", cancellationToken);
            return null;
        }

        var check = await signInManager.CheckPasswordSignInAsync(account, password, lockoutOnFailure: true);
        var user = await users.FindAsync(UserId.From(account.Id), cancellationToken);
        if (!check.Succeeded || user is not { IsActive: true })
        {
            var reason = check.IsLockedOut ? "Locked out after too many failed attempts."
                : !check.Succeeded ? "Wrong password."
                : "User is deactivated.";
            await RecordFailureAsync(UserId.From(account.Id), user?.UserName ?? userName, reason, cancellationToken);
            return null;
        }

        await signInManager.SignInAsync(account, isPersistent: rememberMe);
        await RecordSignInAsync(audit, user, "Password", cancellationToken);
        return user;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        // Recorded first: after signing out the request no longer says who it was.
        if (currentUser.Id is { } id && await users.FindAsync(id, cancellationToken) is { } user)
        {
            await audit.RecordAsync(
                AuditAction.SignedOut, AuditOutcome.Succeeded, new UserAuditTarget(user.Id, user.UserName), null, cancellationToken);
        }

        await signInManager.SignOutAsync();
    }

    /// <summary>Records a successful sign-in; the request has no session yet, so the user is named explicitly.</summary>
    public static Task RecordSignInAsync(IAuditTrail audit, User user, string method, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            AuditAction.SignedIn, AuditOutcome.Succeeded, new UserAuditTarget(user.Id, user.UserName), method, cancellationToken,
            new AuditPerson(user.Id, user.UserName, AuditChannel.Panel));

    /// <summary>A failed sign-in, by the name it gave: whose account someone is trying matters most.</summary>
    private Task RecordFailureAsync(UserId? userId, string userName, string reason, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            AuditAction.SignInFailed, AuditOutcome.Failed, new UserAuditTarget(userId, userName.Trim()), $"Password. {reason}",
            cancellationToken, new AuditPerson(userId, userName.Trim(), AuditChannel.Anonymous));

    /// <summary>
    /// Changes the signed-in user's password. The new security stamp ends all their sessions; the current one is
    /// signed in again.
    /// </summary>
    /// <exception cref="PasswordRejectedException">The current password is wrong, or the new one fails the policy.</exception>
    public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        audit.RunAsync(
            new AuditDraft(AuditAction.PasswordChanged),
            () => ChangeOwnPasswordAsync(currentPassword, newPassword, cancellationToken),
            cancellationToken);

    private async Task ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);

        // Users made through the dev endpoints have no account and so no password to change.
        var account = await Accounts.FindByIdAsync(user.Id.Value.ToString())
            ?? throw new PasswordRejectedException([IncorrectPassword]);

        var result = await Accounts.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            throw new PasswordRejectedException([.. result.Errors.Select(e => e.Description)]);
        }

        await signInManager.RefreshSignInAsync(account);
    }
}
