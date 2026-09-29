using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// Who is making the current request. The panel resolves it from the session cookie, the Discord bot from
/// the Discord user it acts for. Null when the request is not authenticated.
/// </summary>
public interface ICurrentUser
{
    UserId? Id { get; }
}

/// <summary>The request is not authenticated, or its user no longer exists. Maps to HTTP 401.</summary>
public sealed class UnauthenticatedException() : Exception("Authentication is required.");

/// <summary>Loads the <see cref="User"/> behind <see cref="ICurrentUser"/> once per request.</summary>
public sealed class CurrentActor(ICurrentUser currentUser, IUserRepository users)
{
    private User? actor;

    /// <exception cref="UnauthenticatedException">Nobody is signed in, or the user was deleted.</exception>
    /// <exception cref="AccessDeniedException">The user is deactivated.</exception>
    public async Task<User> GetAsync(CancellationToken cancellationToken)
    {
        if (actor is not null)
        {
            return actor;
        }

        if (currentUser.Id is not { } id || await users.FindAsync(id, cancellationToken) is not { } user)
        {
            throw new UnauthenticatedException();
        }

        if (!user.IsActive)
        {
            throw new AccessDeniedException($"User '{user.UserName}' is deactivated.");
        }

        return actor = user;
    }

    /// <exception cref="AccessDeniedException">The user is not an administrator.</exception>
    public async Task<User> GetAdministratorAsync(CancellationToken cancellationToken)
    {
        var user = await GetAsync(cancellationToken);
        return user.IsAdmin ? user : throw new AccessDeniedException("Only administrators may do this.");
    }
}
