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
    /// <param name="except">A repository to ignore, for a rename.</param>
    Task<bool> NameExistsAsync(string name, RepositoryId? except, CancellationToken cancellationToken);
}

/// <summary>An access token as the administrator entered it.</summary>
/// <param name="UserName">Null or blank for <see cref="GitRepository.DefaultAccessUserName"/>.</param>
public sealed record RepositoryAccessToken(string? UserName, string Token)
{
    public const int MaxTokenLength = 1024;

    // Records print every property; the token must not reach a log through ToString.
    public override string ToString() => $"{nameof(RepositoryAccessToken)} {{ UserName = {UserName} }}";
}

/// <summary>
/// Git repositories stacks are deployed from. Only administrators add and change them: a repository may hold every
/// environment's stacks, and its access token reads all of them. Editors may list them, to choose one for a stack.
/// The token is encrypted before it is stored and is never returned.
/// </summary>
public sealed class RepositoryService(
    CurrentActor currentActor,
    IGitRepositoryRepository repositories,
    IRoleAssignmentRepository roleAssignments,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <param name="accessToken">Null for a public repository.</param>
    public async Task<GitRepository> CreateAsync(
        string name, string url, RepositoryAccessToken? accessToken, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = GitRepository.Create(name, url, time.GetUtcNow());
        if (accessToken is not null)
        {
            SetAccessToken(repository, accessToken);
        }

        await EnsureNameIsFreeAsync(repository, cancellationToken);
        repositories.Add(repository);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return repository;
    }

    /// <summary>
    /// Renames the repository and changes its URL. Stacks keep using it: the next deployment fetches from the new URL.
    /// A URL on another host is rejected while a token is set (<see cref="GitRepository.ChangeUrl"/>).
    /// </summary>
    public async Task<GitRepository> UpdateAsync(RepositoryId id, string name, string url, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = await repositories.GetAsync(id, cancellationToken);
        repository.Rename(name);
        repository.ChangeUrl(url);
        await EnsureNameIsFreeAsync(repository, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return repository;
    }

    /// <summary>Sets or replaces the access token; the repository becomes private.</summary>
    public async Task<GitRepository> SetAccessTokenAsync(
        RepositoryId id, RepositoryAccessToken accessToken, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = await repositories.GetAsync(id, cancellationToken);
        SetAccessToken(repository, accessToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return repository;
    }

    /// <summary>Forgets the access token; the repository is fetched as a public one.</summary>
    public async Task<GitRepository> RemoveAccessTokenAsync(RepositoryId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = await repositories.GetAsync(id, cancellationToken);
        repository.RemoveAccessToken();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return repository;
    }

    /// <summary>
    /// Generates a new webhook secret, replacing the previous one: deliveries signed with it are rejected from now on.
    /// The secret is returned only here, to paste into the git server's webhook settings.
    /// </summary>
    public async Task<(GitRepository Repository, string Secret)> CreateWebhookSecretAsync(
        RepositoryId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = await repositories.GetAsync(id, cancellationToken);
        var secret = SecretToken.Generate();
        repository.SetWebhookSecret(protector.Protect(SecretPurpose.WebhookSecret, secret));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (repository, secret);
    }

    /// <summary>Stops deploying on push: every delivery for the repository is rejected.</summary>
    public async Task<GitRepository> RemoveWebhookSecretAsync(RepositoryId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var repository = await repositories.GetAsync(id, cancellationToken);
        repository.RemoveWebhookSecret();
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

    private void SetAccessToken(GitRepository repository, RepositoryAccessToken accessToken)
    {
        // A token is sent in an HTTP header: whitespace would be part of it, and is always a copy-paste mistake.
        var token = accessToken.Token;
        if (token.Length is 0 or > RepositoryAccessToken.MaxTokenLength || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new DomainValidationException(
                $"Access token must be 1-{RepositoryAccessToken.MaxTokenLength} characters without spaces.");
        }

        repository.SetAccessToken(accessToken.UserName, protector.Protect(SecretPurpose.GitAccessToken, token));
    }

    private async Task EnsureNameIsFreeAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        if (await repositories.NameExistsAsync(repository.Name, repository.Id, cancellationToken))
        {
            throw new DomainConflictException($"Repository '{repository.Name}' already exists.");
        }
    }
}
