using System.Diagnostics.CodeAnalysis;

namespace ContainerForest.Aggregator.Domain.Deployments;

/// <summary>
/// Image reference from a compose file, e.g. "ghcr.io/org/app:1.4", "nginx" or "redis@sha256:...".
/// Only splits the reference into name, tag and digest; the registry checks the rest.
/// </summary>
public sealed record ImageReference
{
    public const string ImplicitTag = "latest";

    private ImageReference(string name, string? tag, string? digest)
    {
        Name = name;
        Tag = tag;
        Digest = digest;
    }

    /// <summary>Repository with an optional registry host, e.g. "ghcr.io/org/app".</summary>
    public string Name { get; }

    /// <summary>Null when the reference has no tag; Docker then uses <see cref="ImplicitTag"/>.</summary>
    public string? Tag { get; }

    /// <summary>Content digest, e.g. "sha256:..."; it pins the exact image whatever the tag says.</summary>
    public string? Digest { get; }

    public bool IsPinned => Digest is not null;

    /// <summary>
    /// The tag moves with every push and nothing pins the image: rolling back the stack files
    /// does not bring back the previous image.
    /// </summary>
    public bool IsFloatingLatest => !IsPinned && (Tag ?? ImplicitTag) == ImplicitTag;

    public static bool TryParse(string? value, [NotNullWhen(true)] out ImageReference? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string? digest = null;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = value[(at + 1)..];
            value = value[..at];
            if (digest.Length == 0)
            {
                return false;
            }
        }

        // A ':' after the last '/' starts the tag; one before it is a registry port ("localhost:5000/app").
        string? tag = null;
        var colon = value.LastIndexOf(':');
        if (colon > value.LastIndexOf('/'))
        {
            tag = value[(colon + 1)..];
            value = value[..colon];
            if (tag.Length == 0)
            {
                return false;
            }
        }

        if (value.Length == 0)
        {
            return false;
        }

        image = new ImageReference(value, tag, digest);
        return true;
    }

    public override string ToString() =>
        Name + (Tag is null ? "" : $":{Tag}") + (Digest is null ? "" : $"@{Digest}");
}
