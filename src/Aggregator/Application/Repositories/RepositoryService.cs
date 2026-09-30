using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
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

/// <summary>Git repositories stacks are deployed from. Administrators only: a repository may hold every environment's stacks.</summary>
public sealed class RepositoryService(
    CurrentActor currentActor, IGitRepositoryRepository repositories, IUnitOfWork unitOfWork, TimeProvider time)
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

    public async Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await repositories.ListAsync(cancellationToken);
    }
}
