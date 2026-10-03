using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Repositories;

/// <summary>
/// Git repository the aggregator fetches stack sources from. One repository can hold several stacks.
/// </summary>
/// <remarks>
/// Only HTTPS is supported: LibGit2Sharp from NuGet has no SSH, and a token must not travel over plain HTTP.
/// A private repository belongs to a <see cref="GitConnection"/>: its URL lies under the connection's prefix, the
/// connection's token is fetched with, and only the environments the connection allows may use the repository
/// (ADR-0006). A repository without a connection is public: fetched anonymously and usable in every environment.
/// The webhook secret, stored encrypted, authenticates push notifications for this repository alone; with it set,
/// a push deploys the stacks of the pushed branch that opted in (<see cref="Stacks.Stack.DeployOnPush"/>).
/// </remarks>
public sealed class GitRepository : Entity<RepositoryId>
{
    public const int MaxNameLength = 64;

    private GitRepository(RepositoryId id, string name, Uri url, GitConnectionId? connectionId, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Url = url;
        ConnectionId = connectionId;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private GitRepository()
    {
        Name = null!;
        Url = null!;
    }

    public string Name { get; private set; }

    public Uri Url { get; private set; }

    /// <summary>The connection that gives access to the repository; null for a public one.</summary>
    public GitConnectionId? ConnectionId { get; private set; }

    /// <summary>Encrypted secret the git server signs webhook deliveries with; null when pushes are not received.</summary>
    public string? ProtectedWebhookSecret { get; private set; }

    public bool HasWebhook => ProtectedWebhookSecret is not null;

    public DateTimeOffset CreatedAt { get; private init; }

    /// <param name="connection">Null for a public repository.</param>
    /// <exception cref="DomainValidationException">The URL is invalid or not under the connection's prefix.</exception>
    public static GitRepository Create(string name, string url, GitConnection? connection, DateTimeOffset now)
    {
        var uri = ParseUrl(url, connection);
        return new(RepositoryId.New(), NormalizeName(name), uri, connection?.Id, now);
    }

    public void Rename(string name) => Name = NormalizeName(name);

    /// <summary>Points the repository at another address, connection or both.</summary>
    /// <param name="connection">Null for a public repository.</param>
    /// <exception cref="DomainValidationException">The URL is invalid or not under the connection's prefix.</exception>
    public void ChangeSource(string url, GitConnection? connection)
    {
        Url = ParseUrl(url, connection);
        ConnectionId = connection?.Id;
    }

    /// <summary>True when stacks of <paramref name="environmentId"/> may deploy from this repository.</summary>
    /// <param name="connection">The repository's connection, null for a public repository.</param>
    public bool IsUsableIn(EnvironmentId environmentId, GitConnection? connection)
    {
        EnsureOwnConnection(connection);
        return connection is null || connection.IsAllowedIn(environmentId);
    }

    /// <exception cref="ArgumentException">The connection is not the repository's own.</exception>
    public void EnsureOwnConnection(GitConnection? connection)
    {
        if (connection?.Id != ConnectionId)
        {
            throw new ArgumentException(
                $"Connection {connection?.Id.ToString() ?? "none"} is not the connection of repository '{Name}'.", nameof(connection));
        }
    }

    /// <summary>Sets or replaces the webhook secret; deliveries signed with the previous one are rejected from now on.</summary>
    public void SetWebhookSecret(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            throw new DomainValidationException("Webhook secret must not be empty; use RemoveWebhookSecret to stop receiving pushes.");
        }

        ProtectedWebhookSecret = protectedSecret;
    }

    /// <summary>Stops receiving pushes: every delivery for the repository alone is rejected.</summary>
    public void RemoveWebhookSecret() => ProtectedWebhookSecret = null;

    private static Uri ParseUrl(string url, GitConnection? connection)
    {
        var uri = GitUrl.Parse(url, "Repository URL");
        if (connection is not null && !connection.Covers(uri))
        {
            throw new DomainValidationException(
                $"Repository URL {uri} is not under {connection.UrlPrefix}, the prefix of connection '{connection.Name}'.");
        }

        return uri;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException(
                $"Repository name must be 1-{MaxNameLength} characters without control characters.");
        }

        return trimmed;
    }
}
