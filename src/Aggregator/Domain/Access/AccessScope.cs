using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// How far a request may reach on top of its user's roles: everything for a sign-in in the panel, less for an API
/// token created with limits. A scope only takes rights away, it never grants any.
/// </summary>
public sealed class AccessScope
{
    private readonly HashSet<EnvironmentId>? environments;

    /// <param name="environments">The only environments the request may reach; null for all of them.</param>
    /// <param name="readOnly">Only <see cref="Permission.View"/> and <see cref="Permission.ViewLogs"/>.</param>
    /// <param name="canDecryptSecrets">May ask the aggregator to decrypt SOPS files (ADR-0010).</param>
    public AccessScope(IEnumerable<EnvironmentId>? environments, bool readOnly, bool canDecryptSecrets = false)
    {
        this.environments = environments?.ToHashSet();
        IsReadOnly = readOnly;
        CanDecryptSecrets = canDecryptSecrets && !readOnly;
    }

    /// <summary>No limits: the user's own rights, including decrypting the secrets they may deploy.</summary>
    public static AccessScope Full { get; } = new(null, readOnly: false, canDecryptSecrets: true);

    public bool IsReadOnly { get; }

    /// <summary>
    /// May have the aggregator decrypt SOPS files of the environments where the user is an Editor (ADR-0010). A session
    /// may; an API token only when it was created with this right, since tokens live in files and CI and leak more often.
    /// </summary>
    public bool CanDecryptSecrets { get; }

    /// <summary>Limited to <see cref="Environments"/>. Such a request has no administrator rights.</summary>
    public bool IsRestricted => environments is not null;

    /// <summary>The environments a restricted scope reaches; null when it reaches all of them.</summary>
    public IReadOnlySet<EnvironmentId>? Environments => environments;

    public bool Reaches(EnvironmentId environmentId) => environments is null || environments.Contains(environmentId);

    public bool Allows(Permission permission) =>
        !IsReadOnly || permission is Permission.View or Permission.ViewLogs;
}
