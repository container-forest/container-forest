using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface IApiTokenRepository : IRepository<ApiToken, ApiTokenId>
{
    Task<ApiToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>The user's tokens, newest first.</summary>
    Task<IReadOnlyList<ApiToken>> ListForUserAsync(UserId userId, CancellationToken cancellationToken);

    Task<int> CountForUserAsync(UserId userId, CancellationToken cancellationToken);
}

/// <summary>A newly created token with its secret, which is never shown again.</summary>
public sealed record CreatedApiToken(ApiToken Token, string Secret);

/// <summary>
/// Personal API tokens. A user manages their own tokens only from a session, never with a token: a limited token must
/// not be able to create a wider one. Administrators can list and revoke anyone's tokens.
/// </summary>
public sealed class ApiTokenService(
    CurrentActor currentActor,
    IApiTokenRepository tokens,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IEnvironmentRepository environments,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public const int MaxTokensPerUser = 50;

    public async Task<IReadOnlyList<ApiToken>> ListMineAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        return await tokens.ListForUserAsync(user.Id, cancellationToken);
    }

    /// <param name="environmentIds">Null for every environment; otherwise environments the user can see.</param>
    /// <param name="canDecryptSecrets">The token may have the aggregator decrypt SOPS files (ADR-0010).</param>
    /// <exception cref="NotFoundException">An environment does not exist or the user cannot see it.</exception>
    /// <exception cref="DomainConflictException">The user already has <see cref="MaxTokensPerUser"/> tokens.</exception>
    public async Task<CreatedApiToken> CreateAsync(
        string name,
        bool readOnly,
        IReadOnlyCollection<EnvironmentId>? environmentIds,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken,
        bool canDecryptSecrets = false)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        await EnsureRoomForTokenAsync(user.Id, cancellationToken);
        await EnsureEnvironmentsVisibleAsync(user, environmentIds, cancellationToken);

        var (token, secret) = ApiToken.Create(
            user.Id, name, readOnly, environmentIds, expiresAt, time.GetUtcNow(), canDecryptSecrets);
        tokens.Add(token);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedApiToken(token, secret);
    }

    /// <exception cref="DomainConflictException">The user already has <see cref="MaxTokensPerUser"/> tokens.</exception>
    public async Task EnsureRoomForTokenAsync(UserId userId, CancellationToken cancellationToken)
    {
        if (await tokens.CountForUserAsync(userId, cancellationToken) >= MaxTokensPerUser)
        {
            throw new DomainConflictException($"A user can have at most {MaxTokensPerUser} API tokens; revoke unused ones first.");
        }
    }

    /// <summary>A token may be limited only to environments its user can see.</summary>
    /// <param name="environmentIds">Null for every environment, which needs no check.</param>
    /// <exception cref="NotFoundException">An environment does not exist or the user cannot see it.</exception>
    public async Task EnsureEnvironmentsVisibleAsync(
        User user, IReadOnlyCollection<EnvironmentId>? environmentIds, CancellationToken cancellationToken)
    {
        if (environmentIds is null)
        {
            return;
        }

        // An environment the user cannot see is as unknown as a missing one.
        var assignments = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
        foreach (var environmentId in environmentIds)
        {
            if (await environments.FindAsync(environmentId, cancellationToken) is not { } environment
                || !AccessPolicy.CanSee(user, environment, assignments))
            {
                throw new NotFoundException("Environment", environmentId);
            }
        }
    }

    /// <exception cref="NotFoundException">The user has no such token.</exception>
    public async Task RevokeMineAsync(ApiTokenId id, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        await RemoveAsync(user.Id, id, cancellationToken);
    }

    /// <summary>Revokes the token the request came with, e.g. when the CLI logs out. Any token may do this.</summary>
    /// <exception cref="DomainValidationException">The request did not come with a token.</exception>
    public async Task RevokeCurrentAsync(CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var id = currentActor.ApiTokenId
            ?? throw new DomainValidationException("The request was not made with an API token.");
        await RemoveAsync(actor.Id, id, cancellationToken);
    }

    /// <exception cref="NotFoundException">The user does not exist.</exception>
    public async Task<IReadOnlyList<ApiToken>> ListForUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var user = await users.GetAsync(userId, cancellationToken);
        return await tokens.ListForUserAsync(user.Id, cancellationToken);
    }

    /// <exception cref="NotFoundException">The user has no such token.</exception>
    public async Task RevokeForUserAsync(UserId userId, ApiTokenId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await RemoveAsync(userId, id, cancellationToken);
    }

    private async Task RemoveAsync(UserId owner, ApiTokenId id, CancellationToken cancellationToken)
    {
        // Someone else's token is as unknown as a missing one.
        if (await tokens.FindAsync(id, cancellationToken) is not { } token || token.UserId != owner)
        {
            throw new NotFoundException(nameof(ApiToken), id);
        }

        tokens.Remove(token);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
