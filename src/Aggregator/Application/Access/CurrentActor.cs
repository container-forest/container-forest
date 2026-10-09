using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// Who is making the current request. The panel resolves it from the session cookie, the Discord bot from
/// the Discord user it acts for, the CLI from its API token. Null when the request is not authenticated.
/// </summary>
public interface ICurrentUser
{
    UserId? Id { get; }

    /// <summary>The limits of the request's credentials; <see cref="AccessScope.Full"/> for a session.</summary>
    AccessScope Scope { get; }

    /// <summary>The API token the request came with; null for a session in the panel or the bot.</summary>
    ApiTokenId? ApiTokenId { get; }
}

/// <summary>The request is not authenticated, or its user no longer exists. Maps to HTTP 401.</summary>
public sealed class UnauthenticatedException() : Exception("Authentication is required.");

/// <summary>Loads the <see cref="Actor"/> behind <see cref="ICurrentUser"/> once per request.</summary>
public sealed class CurrentActor(ICurrentUser currentUser, IUserRepository users)
{
    private Actor? actor;

    /// <summary>The API token of the request, if it came with one.</summary>
    public ApiTokenId? ApiTokenId => currentUser.ApiTokenId;

    /// <exception cref="UnauthenticatedException">Nobody is signed in, or the user was deleted.</exception>
    /// <exception cref="AccessDeniedException">The user is deactivated.</exception>
    public async Task<Actor> GetAsync(CancellationToken cancellationToken)
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

        return actor = new Actor(user, currentUser.Scope);
    }

    /// <exception cref="AccessDeniedException">The user is not an administrator, or the request's token is limited to some environments.</exception>
    public async Task<Actor> GetAdministratorAsync(CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        if (current.IsAdmin)
        {
            return current;
        }

        throw new AccessDeniedException(current.User.IsAdmin
            ? "This API token is limited to some environments; administration needs a token without that limit."
            : "Only administrators may do this.");
    }

    /// <summary>
    /// The user of a session in the panel or the bot, not of an API token: for changes to the account itself
    /// (passwords, tokens, the Discord link). A token, even a limited one, must not be able to mint wider credentials.
    /// </summary>
    /// <exception cref="AccessDeniedException">The request came with an API token.</exception>
    public async Task<User> GetSignedInUserAsync(CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        return currentUser.ApiTokenId is null
            ? current.User
            : throw new AccessDeniedException("API tokens cannot change the account; sign in to the panel.");
    }
}
