using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Repositories;

public interface IGitRepositoryRepository : IRepository<GitRepository, RepositoryId>
{
    /// <summary>In name order.</summary>
    Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Case-insensitive.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Git repositories stacks are deployed from. Only administrators add them: a repository may hold every
/// environment's stacks. Editors may list them, to choose one for a stack.
/// </summary>
public sealed class RepositoryService(
    CurrentActor currentActor,
    IGitRepositoryRepository repositories,
    IRoleAssignmentRepository roleAssignments,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Adds a public repository. Private repositories need an access token, which is not supported yet.</summary>
    public async Task<GitRepository> CreateAsync(string name, string url, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = GitRepository.Create(name, url, protectedAccessToken: null, time.GetUtcNow());
        if (await repositories.NameExistsAsync(repository.Name, cancellationToken))
        {
            throw new DomainConflictException($"Repository '{repository.Name}' already exists.");
        }

        repositories.Add(repository);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return repository;
    }

    /// <summary>In name order; for administrators and for users with the Editor role on any environment or stack.</summary>
    public async Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        if (!actor.IsAdmin
            && !(await roleAssignments.ListForUserAsync(actor.Id, cancellationToken)).Any(a => a.Role >= Role.Editor))
        {
            throw new AccessDeniedException("Only administrators and editors may list repositories.");
        }

        return await repositories.ListAsync(cancellationToken);
    }
}
