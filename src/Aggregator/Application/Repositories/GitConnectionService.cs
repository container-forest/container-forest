using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Repositories;

public interface IGitConnectionRepository : IRepository<GitConnection, GitConnectionId>
{
    Task<IReadOnlyList<GitConnection>> ListAsync(CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(string name, GitConnectionId? except, CancellationToken cancellationToken);
}

/// <summary>A token for HTTPS basic authentication, as an administrator typed it.</summary>
/// <param name="UserName">Null or blank for <see cref="GitConnection.DefaultAccessUserName"/>.</param>
public sealed record GitAccessToken(string? UserName, string Token)
{
    public const int MaxTokenLength = 1024;

    // Records print every property; the token must not reach a log through ToString.
    public override string ToString() => $"{nameof(GitAccessToken)} {{ UserName = {UserName} }}";
}

/// <param name="AllEnvironments">Every environment may deploy from the connection's repositories.</param>
/// <param name="EnvironmentIds">The environments that may, when not <paramref name="AllEnvironments"/>.</param>
public sealed record ConnectionEnvironments(bool AllEnvironments, IReadOnlyCollection<EnvironmentId> EnvironmentIds);

/// <param name="RepositoryCount">How many repositories use the connection.</param>
/// <param name="EnvironmentIds">
/// The allowed environments the current user may know of: all of them for administrators, the editor's own for editors.
/// </param>
public sealed record GitConnectionSummary(GitConnection Connection, int RepositoryCount, IReadOnlyCollection<EnvironmentId> EnvironmentIds)
{
    /// <summary>For administrators, who see every allowed environment.</summary>
    public static GitConnectionSummary Full(GitConnection connection, int repositoryCount) =>
        new(connection, repositoryCount, connection.AllowedEnvironments);
}

/// <summary>
/// Connections to git servers (ADR-0006): only administrators create and change them, since a connection lets its
/// environments deploy whatever its token can read. Editors list the connections allowed in their environments,
/// to add repositories under them.
/// </summary>
public sealed class GitConnectionService(
    CurrentActor currentActor,
    RepositoryAccess access,
    IGitConnectionRepository connections,
    IGitRepositoryRepository repositories,
    IEnvironmentRepository environments,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    /// <summary>In name order: all of them for administrators, those allowed in an editor's environments for editors.</summary>
    public async Task<IReadOnlyList<GitConnectionSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var actor = await access.GetActorAsync(cancellationToken);
        var counts = (await repositories.ListAsync(cancellationToken))
            .Where(r => r.ConnectionId is not null)
            .GroupBy(r => r.ConnectionId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return (await connections.ListAsync(cancellationToken))
            .Where(c => actor.CanUse(c))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new GitConnectionSummary(
                c,
                counts.GetValueOrDefault(c.Id),
                actor.IsAdmin ? c.AllowedEnvironments : c.AllowedEnvironments.Where(actor.EditorEnvironments.Contains).ToList()))
            .ToList();
    }

    /// <param name="accessToken">Null for anonymous access, e.g. to scope a public server to some environments.</param>
    public async Task<GitConnectionSummary> CreateAsync(
        string name,
        string urlPrefix,
        GitAccessToken? accessToken,
        ConnectionEnvironments allowed,
        CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var connection = GitConnection.Create(name, urlPrefix, time.GetUtcNow());
        await EnsureNameIsFreeAsync(connection, cancellationToken);
        await AllowAsync(connection, allowed, cancellationToken);
        if (accessToken is not null)
        {
            SetAccessToken(connection, accessToken);
        }

        connections.Add(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return GitConnectionSummary.Full(connection, 0);
    }

    /// <exception cref="DomainConflictException">A repository of the connection would be left outside the new prefix.</exception>
    public async Task<GitConnectionSummary> UpdateAsync(
        GitConnectionId id, string name, string urlPrefix, ConnectionEnvironments allowed, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var connection = await connections.GetAsync(id, cancellationToken);
        var own = await repositories.ListByConnectionAsync(id, cancellationToken);
        connection.Rename(name);
        connection.ChangeUrlPrefix(urlPrefix, own.Select(r => r.Url));
        await EnsureNameIsFreeAsync(connection, cancellationToken);
        await AllowAsync(connection, allowed, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return GitConnectionSummary.Full(connection, own.Count);
    }

    /// <exception cref="DomainConflictException">Repositories still use the connection.</exception>
    public async Task DeleteAsync(GitConnectionId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var connection = await connections.GetAsync(id, cancellationToken);
        var own = await repositories.ListByConnectionAsync(id, cancellationToken);
        if (own.Count > 0)
        {
            throw new DomainConflictException(
                $"Connection '{connection.Name}' is used by {own.Count} repositories; move or change them first.");
        }

        connections.Remove(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public Task<GitConnectionSummary> SetAccessTokenAsync(
        GitConnectionId id, GitAccessToken accessToken, CancellationToken cancellationToken) =>
        ChangeAsync(id, connection => SetAccessToken(connection, accessToken), cancellationToken);

    /// <summary>Forgets the access token; the connection's repositories are fetched anonymously.</summary>
    public Task<GitConnectionSummary> RemoveAccessTokenAsync(GitConnectionId id, CancellationToken cancellationToken) =>
        ChangeAsync(id, connection => connection.RemoveAccessToken(), cancellationToken);

    /// <summary>
    /// Sets the token the aggregator pushes image update commits with (ADR-0015). Recorded in the audit log: it lets the
    /// aggregator write to the connection's repositories. Best a service user that may write only to deployment repositories.
    /// </summary>
    public Task<GitConnectionSummary> SetPushCredentialAsync(
        GitConnectionId id, GitAccessToken pushToken, CancellationToken cancellationToken) =>
        AuditedPushAsync(id, "Push token set", connection =>
        {
            ValidateToken(pushToken.Token);
            connection.SetPushCredential(pushToken.UserName, protector.Protect(SecretPurpose.GitPushToken, pushToken.Token));
        }, cancellationToken);

    /// <summary>Forgets the push token; image updates of the connection's repositories stop.</summary>
    public Task<GitConnectionSummary> RemovePushCredentialAsync(GitConnectionId id, CancellationToken cancellationToken) =>
        AuditedPushAsync(id, "Push token removed", connection => connection.RemovePushCredential(), cancellationToken);

    private Task<GitConnectionSummary> AuditedPushAsync(
        GitConnectionId id, string details, Action<GitConnection> change, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.GitPushCredentialChanged, new SettingsAuditTarget("GitConnection")) { Details = details };
        return audit.RunAsync(draft, async () =>
        {
            var summary = await ChangeAsync(id, change, cancellationToken);
            draft.Target = new SettingsAuditTarget($"GitConnection:{summary.Connection.Name}");
            return summary;
        }, cancellationToken);
    }

    /// <summary>
    /// Generates a new secret for an organization-wide webhook, replacing the previous one. Returned only here, to paste
    /// into the git server's webhook settings.
    /// </summary>
    public async Task<(GitConnectionSummary Connection, string Secret)> CreateWebhookSecretAsync(
        GitConnectionId id, CancellationToken cancellationToken)
    {
        var secret = SecretToken.Generate();
        var summary = await ChangeAsync(
            id, connection => connection.SetWebhookSecret(protector.Protect(SecretPurpose.WebhookSecret, secret)), cancellationToken);
        return (summary, secret);
    }

    public Task<GitConnectionSummary> RemoveWebhookSecretAsync(GitConnectionId id, CancellationToken cancellationToken) =>
        ChangeAsync(id, connection => connection.RemoveWebhookSecret(), cancellationToken);

    private async Task<GitConnectionSummary> ChangeAsync(
        GitConnectionId id, Action<GitConnection> change, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var connection = await connections.GetAsync(id, cancellationToken);
        change(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return GitConnectionSummary.Full(connection, (await repositories.ListByConnectionAsync(id, cancellationToken)).Count);
    }

    private async Task AllowAsync(GitConnection connection, ConnectionEnvironments allowed, CancellationToken cancellationToken)
    {
        if (!allowed.AllEnvironments)
        {
            foreach (var environmentId in allowed.EnvironmentIds)
            {
                _ = await environments.FindAsync(environmentId, cancellationToken)
                    ?? throw new DomainValidationException($"Environment {environmentId} does not exist.");
            }
        }

        connection.AllowEnvironments(allowed.AllEnvironments, allowed.EnvironmentIds);
    }

    private void SetAccessToken(GitConnection connection, GitAccessToken accessToken)
    {
        ValidateToken(accessToken.Token);
        connection.SetAccessToken(accessToken.UserName, protector.Protect(SecretPurpose.GitAccessToken, accessToken.Token));
    }

    private static void ValidateToken(string token)
    {
        // A token is sent in an HTTP header: whitespace would be part of it, and is always a copy-paste mistake.
        if (token.Length is 0 or > GitAccessToken.MaxTokenLength || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new DomainValidationException(
                $"Access token must be 1-{GitAccessToken.MaxTokenLength} characters without spaces.");
        }
    }

    private async Task EnsureNameIsFreeAsync(GitConnection connection, CancellationToken cancellationToken)
    {
        if (await connections.NameExistsAsync(connection.Name, connection.Id, cancellationToken))
        {
            throw new DomainConflictException($"Connection '{connection.Name}' already exists.");
        }
    }
}
