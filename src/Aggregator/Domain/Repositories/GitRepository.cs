using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Repositories;

/// <summary>
/// Git repository the aggregator fetches stack sources from. One repository can hold several stacks.
/// </summary>
/// <remarks>
/// Only HTTPS is supported: LibGit2Sharp from NuGet has no SSH, and a deploy token must not travel over plain HTTP.
/// The access token is stored already encrypted by the infrastructure layer; the domain never sees it in plain text.
/// </remarks>
public sealed class GitRepository : Entity<RepositoryId>
{
    public const int MaxNameLength = 64;

    private GitRepository(RepositoryId id, string name, Uri url, string? protectedAccessToken, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Url = url;
        ProtectedAccessToken = protectedAccessToken;
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

    public DateTimeOffset CreatedAt { get; private init; }

    public static GitRepository Create(string name, string url, string? protectedAccessToken, DateTimeOffset now) =>
        new(RepositoryId.New(), NormalizeName(name), ParseUrl(url), protectedAccessToken, now);

    public void Rename(string name) => Name = NormalizeName(name);

    public void ChangeUrl(string url) => Url = ParseUrl(url);

    public void SetAccessToken(string protectedAccessToken)
    {
        if (string.IsNullOrWhiteSpace(protectedAccessToken))
        {
            throw new DomainValidationException("Access token must not be empty; use RemoveAccessToken for a public repository.");
        }

        ProtectedAccessToken = protectedAccessToken;
    }

    public void RemoveAccessToken() => ProtectedAccessToken = null;

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
