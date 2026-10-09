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
    public AccessScope(IEnumerable<EnvironmentId>? environments, bool readOnly)
    {
        this.environments = environments?.ToHashSet();
        IsReadOnly = readOnly;
    }

    /// <summary>No limits: the user's own rights.</summary>
    public static AccessScope Full { get; } = new(null, readOnly: false);

    public bool IsReadOnly { get; }

    /// <summary>Limited to <see cref="Environments"/>. Such a request has no administrator rights.</summary>
    public bool IsRestricted => environments is not null;

    /// <summary>The environments a restricted scope reaches; null when it reaches all of them.</summary>
    public IReadOnlySet<EnvironmentId>? Environments => environments;

    public bool Reaches(EnvironmentId environmentId) => environments is null || environments.Contains(environmentId);

    public bool Allows(Permission permission) =>
        !IsReadOnly || permission is Permission.View or Permission.ViewLogs;
}
