using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Repositories;

/// <summary>
/// Git repository the aggregator fetches stack sources from. One repository can hold several stacks.
/// </summary>
/// <remarks>
/// Only HTTPS is supported: LibGit2Sharp from NuGet has no SSH, and a deploy token must not travel over plain HTTP.
/// The access token is stored already encrypted by the application layer; the domain never sees it in plain text.
/// The token belongs to the URL's host: a URL on another host is accepted only after the token is removed, so a
/// changed URL cannot send the token to a server it was not issued for.
/// </remarks>
public sealed class GitRepository : Entity<RepositoryId>
{
    public const int MaxNameLength = 64;

    public const int MaxAccessUserNameLength = 128;

    /// <summary>
    /// User name sent with a token when none is set. GitHub and GitLab accept any user name with a token;
    /// others (GitLab deploy tokens, Bitbucket) need a specific one.
    /// </summary>
    public const string DefaultAccessUserName = "x-access-token";

    private GitRepository(RepositoryId id, string name, Uri url, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Url = url;
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

    /// <summary>Encrypted deploy token; null for a public repository.</summary>
    public string? ProtectedAccessToken { get; private set; }

    /// <summary>User name sent with the token; null for <see cref="DefaultAccessUserName"/> or without a token.</summary>
    public string? AccessUserName { get; private set; }

    public bool HasAccessToken => ProtectedAccessToken is not null;

    public DateTimeOffset CreatedAt { get; private init; }

    public static GitRepository Create(string name, string url, DateTimeOffset now) =>
        new(RepositoryId.New(), NormalizeName(name), ParseUrl(url), now);

    public void Rename(string name) => Name = NormalizeName(name);

    /// <exception cref="DomainValidationException">The URL is invalid, or it is on another host while a token is set.</exception>
    public void ChangeUrl(string url)
    {
        var uri = ParseUrl(url);
        if (HasAccessToken && !string.Equals(uri.Authority, Url.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                $"The access token was set for {Url.Authority}; remove it before moving the repository to {uri.Authority}.");
        }

        Url = uri;
    }

    /// <param name="userName">Null or blank for <see cref="DefaultAccessUserName"/>.</param>
    public void SetAccessToken(string? userName, string protectedAccessToken)
    {
        if (string.IsNullOrWhiteSpace(protectedAccessToken))
        {
            throw new DomainValidationException("Access token must not be empty; use RemoveAccessToken for a public repository.");
        }

        AccessUserName = NormalizeAccessUserName(userName);
        ProtectedAccessToken = protectedAccessToken;
    }

    public void RemoveAccessToken()
    {
        ProtectedAccessToken = null;
        AccessUserName = null;
    }

    private static Uri ParseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainValidationException($"'{url}' is not an absolute https:// URL.");
        }

        // Credentials in the URL would end up in logs and API responses.
        if (uri.UserInfo.Length > 0)
        {
            throw new DomainValidationException("Repository URL must not contain credentials; set an access token instead.");
        }

        return uri;
    }

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
                $"Repository name must be 1-{MaxNameLength} characters without control characters.");
        }

        return trimmed;
    }
}
