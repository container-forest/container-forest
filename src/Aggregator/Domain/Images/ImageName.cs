using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Domain.Images;

/// <summary>
/// An image repository without a tag or digest, normalized as Docker resolves it: "nginx" is "docker.io/library/nginx",
/// "ghcr.io/Org/App" is not valid (repositories are lower case). Two names are the same image exactly when they are equal,
/// so a policy matches compose services by equality rather than by a substring (ADR-0015).
/// </summary>
public sealed partial record ImageName
{
    public const int MaxLength = 255;

    private ImageName(string registry, string repository)
    {
        Registry = registry;
        Repository = repository;
    }

    /// <summary>The registry host, see <see cref="RegistryHost"/>; "docker.io" for Docker Hub.</summary>
    public string Registry { get; }

    /// <summary>The path in the registry, e.g. "lepterion/ananasik/ananasik" or "library/nginx".</summary>
    public string Repository { get; }

    /// <summary>The full name, e.g. "gitea.rup.rocks/lepterion/ananasik/ananasik" or "docker.io/library/nginx".</summary>
    public string Value => $"{Registry}/{Repository}";

    /// <exception cref="DomainValidationException">Not an image name, or one with a tag, digest or variables.</exception>
    public static ImageName Parse(string? value)
    {
        var text = value?.Trim();
        if (text is null || text.Length is 0 or > MaxLength || text.Contains('$', StringComparison.Ordinal)
            || !ImageReference.TryParse(text, out var reference) || reference.Tag is not null || reference.Digest is not null
            || !TryFrom(reference, out var name))
        {
            throw new DomainValidationException(
                $"'{value}' is not an image name: give the repository without a tag or digest, e.g. ghcr.io/org/app.");
        }

        return name;
    }

    /// <summary>The repository of an image reference from a compose file; false for one compose would interpolate.</summary>
    public static bool TryFrom(ImageReference reference, [NotNullWhen(true)] out ImageName? name)
    {
        name = null;
        if (reference.Name.Contains('$', StringComparison.Ordinal))
        {
            return false;
        }

        var registry = RegistryHost.Of(reference);
        var slash = reference.Name.IndexOf('/', StringComparison.Ordinal);
        var path = registry == RegistryHost.DockerHub && !HasHostPart(reference.Name)
            ? reference.Name
            : reference.Name[(slash + 1)..];
        if (registry == RegistryHost.DockerHub && !path.Contains('/', StringComparison.Ordinal))
        {
            path = "library/" + path;
        }

        if (!PathPattern().IsMatch(path))
        {
            return false;
        }

        name = new ImageName(registry, path);
        return true;
    }

    public override string ToString() => Value;

    private static bool HasHostPart(string name)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return false;
        }

        var first = name[..slash];
        return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";
    }

    // The OCI distribution grammar for repository names: lowercase components separated by '/'.
    [GeneratedRegex(@"^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
