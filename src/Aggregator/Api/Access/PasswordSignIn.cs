using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>Signs users in and out with a user name and password, and changes the signed-in user's password.</summary>
internal sealed class PasswordSignIn(SignInManager<AccountUser> signInManager, IUserRepository users, CurrentActor currentActor)
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
            return null;
        }

        var check = await signInManager.CheckPasswordSignInAsync(account, password, lockoutOnFailure: true);
        var user = await users.FindAsync(UserId.From(account.Id), cancellationToken);
        if (!check.Succeeded || user is not { IsActive: true })
        {
            return null;
        }

        await signInManager.SignInAsync(account, isPersistent: rememberMe);
        return user;
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();

    /// <summary>
    /// Changes the signed-in user's password. The new security stamp ends all their sessions; the current one is
    /// signed in again.
    /// </summary>
    /// <exception cref="PasswordRejectedException">The current password is wrong, or the new one fails the policy.</exception>
    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
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
