using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Repositories;

public interface IGitRepositoryRepository : IRepository<GitRepository, RepositoryId>
{
    Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GitRepository>> ListByConnectionAsync(GitConnectionId connectionId, CancellationToken cancellationToken);
}

/// <param name="Connection">Null for a public repository.</param>
/// <param name="CanManage">Whether the current user may change, remove the repository and its webhook.</param>
/// <param name="StackCount">
/// How many stacks deploy from it, in all environments; only for those who may manage it (they may deploy each of
/// them), null otherwise. A repository with stacks cannot be removed.
/// </param>
public sealed record RepositorySummary(GitRepository Repository, GitConnection? Connection, bool CanManage, int? StackCount = null);

/// <summary>
/// Repositories as the current user may see and change them (ADR-0006). Administrators manage all of them. Editors add
/// public repositories and repositories under the connections allowed in their environments, and change those whose
/// stacks they may all deploy. Tokens belong to connections (<see cref="GitConnectionService"/>), not to repositories.
/// </summary>
public sealed class RepositoryService(
    RepositoryAccess access,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
    IStackRepository stacks,
    IGitSource gitSource,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <param name="connectionId">Null for a public repository.</param>
    /// <exception cref="NotFoundException">The connection does not exist or the user may not use it.</exception>
    public async Task<RepositorySummary> CreateAsync(
        string name, string url, GitConnectionId? connectionId, CancellationToken cancellationToken)
    {
        var actor = await access.GetActorAsync(cancellationToken);
        var connection = await UsableConnectionAsync(actor, connectionId, cancellationToken);

        var repository = GitRepository.Create(name, url, connection, time.GetUtcNow());
        repositories.Add(repository);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new RepositorySummary(repository, connection, CanManage: true, StackCount: 0);
    }

    /// <summary>Renames the repository and points it at another address or connection; its stacks deploy from there next.</summary>
    /// <exception cref="NotFoundException">The repository or the new connection is not visible to the user.</exception>
    /// <exception cref="AccessDeniedException">The user may not deploy every stack of the repository.</exception>
    public async Task<RepositorySummary> UpdateAsync(
        RepositoryId id, string name, string url, GitConnectionId? connectionId, CancellationToken cancellationToken)
    {
        var (actor, repository, _) = await LoadManagedAsync(id, cancellationToken);
        var connection = await UsableConnectionAsync(actor, connectionId, cancellationToken);

        repository.Rename(name);
        repository.ChangeSource(url, connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ManagedSummaryAsync(repository, connection, cancellationToken);
    }

    /// <summary>
    /// Generates a new webhook secret, replacing the previous one: deliveries signed with it are rejected from now on.
    /// The secret is returned only here, to paste into the git server's webhook settings.
    /// </summary>
    public async Task<(RepositorySummary Repository, string Secret)> CreateWebhookSecretAsync(
        RepositoryId id, CancellationToken cancellationToken)
    {
        var (_, repository, connection) = await LoadManagedAsync(id, cancellationToken);
        var secret = SecretToken.Generate();
        repository.SetWebhookSecret(protector.Protect(SecretPurpose.WebhookSecret, secret));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await ManagedSummaryAsync(repository, connection, cancellationToken), secret);
    }

    /// <summary>Stops deploying on push through the repository's own webhook.</summary>
    public async Task<RepositorySummary> RemoveWebhookSecretAsync(RepositoryId id, CancellationToken cancellationToken)
    {
        var (_, repository, connection) = await LoadManagedAsync(id, cancellationToken);
        repository.RemoveWebhookSecret();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ManagedSummaryAsync(repository, connection, cancellationToken);
    }

    /// <summary>
    /// Removes a repository no stack deploys from, with its webhook secret and the aggregator's cached clone. Its
    /// stacks must first be pointed at another repository or removed: the count does not name them, since they may be
    /// in environments the user does not see.
    /// </summary>
    /// <exception cref="NotFoundException">The repository is not visible to the user.</exception>
    /// <exception cref="AccessDeniedException">The user may not manage it.</exception>
    /// <exception cref="DomainConflictException">Stacks deploy from it.</exception>
    public async Task RemoveAsync(RepositoryId id, CancellationToken cancellationToken)
    {
        var (_, repository, _) = await LoadManagedAsync(id, cancellationToken);
        var stackCount = (await stacks.ListByRepositoryAsync(repository.Id, cancellationToken)).Count;
        if (stackCount > 0)
        {
            throw new DomainConflictException(
                $"Repository '{repository.Name}' is used by {stackCount} stack(s); point them at another repository or remove them first.");
        }

        repositories.Remove(repository);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // After saving: the clone is only a cache, and a failure to delete it must not keep the repository.
        await gitSource.ForgetAsync(repository.Id, cancellationToken);
    }

    /// <summary>
    /// In name order: all of them for administrators; for editors, those their environments may deploy from.
    /// </summary>
    /// <exception cref="AccessDeniedException">The user is neither an administrator nor an Editor anywhere.</exception>
    public async Task<IReadOnlyList<RepositorySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var actor = await access.GetActorAsync(cancellationToken);
        var byId = (await connections.ListAsync(cancellationToken)).ToDictionary(c => c.Id);

        var result = new List<RepositorySummary>();
        foreach (var repository in await repositories.ListAsync(cancellationToken))
        {
            var connection = repository.ConnectionId is { } connectionId ? byId.GetValueOrDefault(connectionId) : null;
            if (actor.CanUse(connection))
            {
                result.Add(await access.CanManageAsync(actor, repository, connection, cancellationToken)
                    ? await ManagedSummaryAsync(repository, connection, cancellationToken)
                    : new RepositorySummary(repository, connection, CanManage: false));
            }
        }

        return result.OrderBy(r => r.Repository.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Repository.Url.ToString()).ToList();
    }

    private async Task<RepositorySummary> ManagedSummaryAsync(
        GitRepository repository, GitConnection? connection, CancellationToken cancellationToken) =>
        new(repository, connection, CanManage: true, (await stacks.ListByRepositoryAsync(repository.Id, cancellationToken)).Count);

    private async Task<GitConnection?> UsableConnectionAsync(
        RepositoryActor actor, GitConnectionId? connectionId, CancellationToken cancellationToken)
    {
        if (connectionId is not { } id)
        {
            return null;
        }

        // Reported as missing, so a connection of other environments does not show up.
        var connection = await connections.FindAsync(id, cancellationToken);
        return connection is not null && actor.CanUse(connection) ? connection : throw new NotFoundException("Git connection", id);
    }

    private async Task<(RepositoryActor Actor, GitRepository Repository, GitConnection? Connection)> LoadManagedAsync(
        RepositoryId id, CancellationToken cancellationToken)
    {
        var actor = await access.GetActorAsync(cancellationToken);
        var repository = await repositories.FindAsync(id, cancellationToken);
        var connection = repository?.ConnectionId is { } connectionId ? await connections.FindAsync(connectionId, cancellationToken) : null;
        if (repository is null || !actor.CanUse(connection))
        {
            throw new NotFoundException("Repository", id);
        }

        if (!await access.CanManageAsync(actor, repository, connection, cancellationToken))
        {
            throw new AccessDeniedException(
                $"Changing repository '{repository.Name}' requires the right to deploy every stack that uses it.");
        }

        return (actor, repository, connection);
    }
}
