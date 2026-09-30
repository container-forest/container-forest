using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ContainerForest.Aggregator.Domain.Deployments;

/// <summary>
/// Registry host names as Docker compares them: "ghcr.io", "registry.example.com:5000", or <see cref="DockerHub"/>
/// for images without a registry part ("nginx", "library/redis").
/// </summary>
public static partial class RegistryHost
{
    public const string DockerHub = "docker.io";

    /// <summary>The key Docker Hub credentials have in a docker config.json; docker login writes it this way.</summary>
    public const string DockerHubConfigKey = "https://index.docker.io/v1/";

    /// <summary>
    /// The registry an image is pulled from. Docker reads the part before the first '/' as a host only when it has
    /// a '.' or a ':' or is "localhost"; otherwise the image is on Docker Hub ("org/app" is Docker Hub's org).
    /// </summary>
    public static string Of(ImageReference image)
    {
        var slash = image.Name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return DockerHub;
        }

        var first = image.Name[..slash];
        var isHost = first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";
        return isHost ? Normalize(first) : DockerHub;
    }

    /// <summary>
    /// Accepts what people paste from `docker login` or registry docs: "ghcr.io", "https://ghcr.io",
    /// "registry.example.com:5000", "index.docker.io", "https://index.docker.io/v1/".
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out string? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var path = slash >= 0 ? text[slash..] : "";
        text = slash >= 0 ? text[..slash] : text;

        var normalized = Normalize(text);
        // A path is part of a registry address only for Docker Hub's legacy "/v1/".
        var pathAllowed = path is "" or "/" || (normalized == DockerHub && path is "/v1" or "/v1/");
        if (!pathAllowed || !HostPattern().IsMatch(normalized))
        {
            return false;
        }

        host = normalized;
        return true;
    }

    /// <summary>The key of the host in a docker config.json "auths" object.</summary>
    public static string ConfigKey(string host) => host == DockerHub ? DockerHubConfigKey : host;

    private static string Normalize(string host)
    {
        var lower = host.ToLowerInvariant();
        return lower is "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" ? DockerHub : lower;
    }

    // DNS name or IPv4 with an optional port; \z instead of $ so a trailing newline is not accepted.
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:[0-9]{1,5})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
