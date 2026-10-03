using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Repositories;

/// <summary>
/// HTTPS addresses of git repositories and of the URL prefixes of <see cref="GitConnection"/>s.
/// </summary>
/// <remarks>
/// Paths are compared segment by segment and case-insensitively, as GitHub, Gitea and GitLab treat owner and
/// repository names, with ".git" at the end of a segment ignored: "team-a/" covers "team-a/app.git" but not "team-ab/app".
/// </remarks>
public static class GitUrl
{
    private const string GitSuffix = ".git";

    /// <summary>Parses an absolute https:// URL without credentials, query, fragment or percent-encoding in the path.</summary>
    /// <param name="what">How the error message names the value, e.g. "Repository URL".</param>
    /// <exception cref="DomainValidationException">The URL is not acceptable.</exception>
    public static Uri Parse(string url, string what)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainValidationException($"{what} '{url}' is not an absolute https:// URL.");
        }

        // Credentials in the URL would end up in logs and API responses.
        if (uri.UserInfo.Length > 0)
        {
            throw new DomainValidationException($"{what} must not contain credentials; set an access token instead.");
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new DomainValidationException($"{what} must not have a query or a fragment.");
        }

        // An encoded slash, backslash or ".." is one plain segment to the prefix comparison but may be a separator or a step up
        // to the git server, which would let a URL under one prefix reach a repository outside it. Git hosts do not
        // allow characters that need encoding in owner and repository names anyway.
        if (uri.AbsolutePath.Contains('%', StringComparison.Ordinal) || uri.AbsolutePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new DomainValidationException($"{what} must not contain percent-encoded characters or backslashes.");
        }

        return uri;
    }

    /// <summary>
    /// True when <paramref name="url"/> is on the prefix's host and port and its path starts with all of the prefix's
    /// path segments. Both must be https.
    /// </summary>
    public static bool Covers(Uri prefix, Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps
            || prefix.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Authority, prefix.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefixSegments = Segments(prefix);
        var urlSegments = Segments(url);
        return prefixSegments.Count <= urlSegments.Count
            && prefixSegments.Select((segment, i) => string.Equals(segment, urlSegments[i], StringComparison.OrdinalIgnoreCase)).All(same => same);
    }

    /// <summary>
    /// The same value for every spelling of one repository's address ("…/Team/App", "…/team/app.git/"), to find a
    /// repository by the address a webhook payload gives.
    /// </summary>
    public static string Key(Uri url) =>
        $"{url.Authority.ToLowerInvariant()}/{string.Join('/', Segments(url)).ToLowerInvariant()}";

    // ".git" is optional in every segment, not only the last: libgit2 asks for credentials with URLs such as
    // "…/team/app.git/info/refs", which a prefix "…/team/app" covers.
    private static List<string> Segments(Uri url) =>
        url.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase) ? segment[..^GitSuffix.Length] : segment)
            .ToList();
}
