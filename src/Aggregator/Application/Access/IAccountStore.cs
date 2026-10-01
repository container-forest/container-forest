using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// The sign-in accounts behind <see cref="User"/>s: passwords, lockout and sessions. Every call that changes an
/// account saves the unit of work, pending domain changes included, so a domain change and its account change
/// are stored together.
/// </summary>
public interface IAccountStore
{
    /// <summary>Creates the account of a new user, saving the user with it.</summary>
    /// <exception cref="PasswordRejectedException">The password fails the policy; nothing is saved.</exception>
    Task CreateAsync(User user, string password, CancellationToken cancellationToken);

    /// <summary>Keeps the account's user name in step with the domain user's. Users without an account are skipped.</summary>
    Task RenameAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the password and ends the user's sessions. A user without an account (made before accounts existed)
    /// gets one.
    /// </summary>
    /// <exception cref="PasswordRejectedException">The password fails the policy; nothing is changed.</exception>
    Task SetPasswordAsync(User user, string password, CancellationToken cancellationToken);

    /// <summary>Ends every session of the user: their session cookies stop working on the next request.</summary>
    Task EndSessionsAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Lifts a lockout after too many failed sign-ins and resets the failure count.</summary>
    Task UnlockAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Accounts locked out at <paramref name="now"/>, with the end of their lockout.</summary>
    Task<IReadOnlyDictionary<UserId, DateTimeOffset>> ListLockoutsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
