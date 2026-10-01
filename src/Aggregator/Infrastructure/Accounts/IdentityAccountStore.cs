using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// <see cref="IAccountStore"/> on ASP.NET Core Identity. The Identity store saves the shared context on every change,
/// so pending domain changes are saved with the account.
/// </summary>
internal sealed class IdentityAccountStore(UserManager<AccountUser> userManager, AggregatorDbContext db) : IAccountStore
{
    public async Task CreateAsync(User user, string password, CancellationToken cancellationToken) =>
        ThrowIfFailed(await userManager.CreateAsync(new AccountUser(user), password));

    public async Task RenameAsync(User user, CancellationToken cancellationToken)
    {
        if (await FindAsync(user.Id) is { } account)
        {
            // Not SetUserNameAsync: it also replaces the security stamp, which would sign the user out.
            account.UserName = user.UserName;
            ThrowIfFailed(await userManager.UpdateAsync(account));
        }
    }

    public async Task SetPasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        if (await FindAsync(user.Id) is not { } account)
        {
            await CreateAsync(user, password, cancellationToken);
            return;
        }

        // Validate before touching the hash: removing the old password first could leave the account without one.
        var errors = new List<IdentityError>();
        foreach (var validator in userManager.PasswordValidators)
        {
            errors.AddRange((await validator.ValidateAsync(userManager, account, password)).Errors);
        }

        if (errors.Count > 0)
        {
            throw new PasswordRejectedException([.. errors.Select(e => e.Description)]);
        }

        account.PasswordHash = userManager.PasswordHasher.HashPassword(account, password);
        ThrowIfFailed(await userManager.UpdateSecurityStampAsync(account));
    }

    public async Task EndSessionsAsync(UserId userId, CancellationToken cancellationToken)
    {
        if (await FindAsync(userId) is { } account)
        {
            ThrowIfFailed(await userManager.UpdateSecurityStampAsync(account));
        }
    }

    public async Task UnlockAsync(UserId userId, CancellationToken cancellationToken)
    {
        if (await FindAsync(userId) is { } account)
        {
            account.LockoutEnd = null;
            account.AccessFailedCount = 0;
            ThrowIfFailed(await userManager.UpdateAsync(account));
        }
    }

    public async Task<IReadOnlyDictionary<UserId, DateTimeOffset>> ListLockoutsAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Compared in memory: there are few locked accounts, and the stored form of LockoutEnd is the converter's.
        var locked = await db.Users
            .Where(a => a.LockoutEnd != null)
            .Select(a => new { a.Id, a.LockoutEnd })
            .ToListAsync(cancellationToken);
        return locked
            .Where(a => a.LockoutEnd > now)
            .ToDictionary(a => UserId.From(a.Id), a => a.LockoutEnd!.Value);
    }

    private Task<AccountUser?> FindAsync(UserId userId) => userManager.FindByIdAsync(userId.Value.ToString());

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var reasons = result.Errors.Select(e => e.Description).ToList();
        if (result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)))
        {
            throw new PasswordRejectedException(reasons);
        }

        throw new InvalidOperationException($"Account change failed: {string.Join(" ", reasons)}");
    }
}
