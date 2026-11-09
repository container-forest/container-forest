using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Repositories;

/// <summary>
/// Access to the repositories of a git server under one URL prefix: an organization ("https://gitea.example.com/team-a/"),
/// a whole server, or a single repository. Holds the credentials the aggregator fetches with and the environments that
/// may deploy from these repositories (ADR-0006).
/// </summary>
/// <remarks>
/// By creating a connection an administrator lets the listed environments deploy whatever its token can read under
/// the prefix; which repositories that is, the git server decides (e.g. the teams of a service user in Gitea, whose
/// tokens are not limited to one repository). Repositories under the connection are then added without a token.
/// The access token and the webhook secret are stored already encrypted by the application layer.
/// </remarks>
public sealed class GitConnection : Entity<GitConnectionId>
{
    public const int MaxNameLength = 64;

    public const int MaxAccessUserNameLength = 128;

    /// <summary>
    /// User name sent with a token when none is set. GitHub, Gitea and GitLab accept any user name with a token;
    /// others (GitLab deploy tokens, Bitbucket) need a specific one.
    /// </summary>
    public const string DefaultAccessUserName = "x-access-token";

    private readonly List<GitConnectionEnvironment> environments = [];

    private GitConnection(GitConnectionId id, string name, Uri urlPrefix, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        UrlPrefix = urlPrefix;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private GitConnection()
    {
        Name = null!;
        UrlPrefix = null!;
    }

    public string Name { get; private set; }

    /// <summary>Repositories of the connection have URLs under it (<see cref="GitUrl.Covers"/>); the token is sent only there.</summary>
    public Uri UrlPrefix { get; private set; }

    /// <summary>Encrypted access token; null when the repositories are fetched anonymously.</summary>
    public string? ProtectedAccessToken { get; private set; }

    /// <summary>User name sent with the token; null for <see cref="DefaultAccessUserName"/> or without a token.</summary>
    public string? AccessUserName { get; private set; }

    public bool HasAccessToken => ProtectedAccessToken is not null;

    /// <summary>
    /// Encrypted token the aggregator pushes image update commits with (ADR-0015); null when it may not write. Separate
    /// from <see cref="ProtectedAccessToken"/>, which should stay read-only (ADR-0006).
    /// </summary>
    public string? ProtectedPushToken { get; private set; }

    /// <summary>User name sent with the push token; null for <see cref="DefaultAccessUserName"/> or without a token.</summary>
    public string? PushUserName { get; private set; }

    public bool HasPushCredential => ProtectedPushToken is not null;

    /// <summary>Every environment, present and future, may deploy from the connection's repositories.</summary>
    public bool AllowsAllEnvironments { get; private set; }

    /// <summary>Environments that may deploy from the connection's repositories; ignored when <see cref="AllowsAllEnvironments"/>.</summary>
    public IReadOnlyCollection<EnvironmentId> AllowedEnvironments => environments.Select(e => e.EnvironmentId).ToList();

    /// <summary>Encrypted secret an organization-wide webhook signs deliveries with; null when pushes are not received.</summary>
    public string? ProtectedWebhookSecret { get; private set; }

    public bool HasWebhook => ProtectedWebhookSecret is not null;

    public DateTimeOffset CreatedAt { get; private init; }

    public static GitConnection Create(string name, string urlPrefix, DateTimeOffset now) =>
        new(GitConnectionId.New(), NormalizeName(name), GitUrl.Parse(urlPrefix, "URL prefix"), now);

    public void Rename(string name) => Name = NormalizeName(name);

    public bool IsAllowedIn(EnvironmentId environmentId) =>
        AllowsAllEnvironments || environments.Any(e => e.EnvironmentId == environmentId);

    /// <summary>True when <paramref name="url"/> lies under <see cref="UrlPrefix"/>, so the connection's token may be sent there.</summary>
    public bool Covers(Uri url) => GitUrl.Covers(UrlPrefix, url);

    /// <param name="repositoryUrls">URLs of the connection's repositories; each must stay under the new prefix.</param>
    /// <exception cref="DomainValidationException">The prefix is invalid, or on another host while a token is set.</exception>
    /// <exception cref="DomainConflictException">A repository of the connection would be left outside the prefix.</exception>
    public void ChangeUrlPrefix(string urlPrefix, IEnumerable<Uri> repositoryUrls)
    {
        var prefix = GitUrl.Parse(urlPrefix, "URL prefix");

        // The token was issued for this server; another one must not receive it.
        if ((HasAccessToken || HasPushCredential) && !string.Equals(prefix.Authority, UrlPrefix.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                $"The tokens were set for {UrlPrefix.Authority}; remove them before moving the connection to {prefix.Authority}.");
        }

        if (repositoryUrls.FirstOrDefault(url => !GitUrl.Covers(prefix, url)) is { } outside)
        {
            throw new DomainConflictException(
                $"Repository {outside} of connection '{Name}' is not under {prefix}; move or change it first.");
        }

        UrlPrefix = prefix;
    }

    /// <param name="all">Every environment; <paramref name="environmentIds"/> is then ignored.</param>
    public void AllowEnvironments(bool all, IEnumerable<EnvironmentId> environmentIds)
    {
        AllowsAllEnvironments = all;
        var wanted = all ? new HashSet<EnvironmentId>() : environmentIds.ToHashSet();
        environments.RemoveAll(e => !wanted.Contains(e.EnvironmentId));
        foreach (var id in wanted.Where(id => environments.All(e => e.EnvironmentId != id)))
        {
            environments.Add(new GitConnectionEnvironment(id));
        }
    }

    /// <param name="userName">Null or blank for <see cref="DefaultAccessUserName"/>.</param>
    public void SetAccessToken(string? userName, string protectedAccessToken)
    {
        if (string.IsNullOrWhiteSpace(protectedAccessToken))
        {
            throw new DomainValidationException("Access token must not be empty; use RemoveAccessToken for anonymous access.");
        }

        AccessUserName = NormalizeAccessUserName(userName);
        ProtectedAccessToken = protectedAccessToken;
    }

    public void RemoveAccessToken()
    {
        ProtectedAccessToken = null;
        AccessUserName = null;
    }

    /// <param name="userName">Null or blank for <see cref="DefaultAccessUserName"/>.</param>
    public void SetPushCredential(string? userName, string protectedPushToken)
    {
        if (string.IsNullOrWhiteSpace(protectedPushToken))
        {
            throw new DomainValidationException("Push token must not be empty; use RemovePushCredential to stop pushing.");
        }

        PushUserName = NormalizeAccessUserName(userName);
        ProtectedPushToken = protectedPushToken;
    }

    public void RemovePushCredential()
    {
        ProtectedPushToken = null;
        PushUserName = null;
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

    public void RemoveWebhookSecret() => ProtectedWebhookSecret = null;

    private static string? NormalizeAccessUserName(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var trimmed = userName.Trim();

        // ':' would split the user name from the password in HTTP basic authentication.
        if (trimmed.Length > MaxAccessUserNameLength || trimmed.Any(c => char.IsControl(c) || c == ':'))
        {
            throw new DomainValidationException(
                $"Access user name must be at most {MaxAccessUserNameLength} characters without ':' or control characters.");
        }

        return trimmed;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException(
                $"Connection name must be 1-{MaxNameLength} characters without control characters.");
        }

        return trimmed;
    }
}

/// <summary>One environment a <see cref="GitConnection"/> is allowed in.</summary>
public sealed class GitConnectionEnvironment
{
    public GitConnectionEnvironment(EnvironmentId environmentId) => EnvironmentId = environmentId;

    public EnvironmentId EnvironmentId { get; private init; }
}
