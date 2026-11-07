using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Policies;

/// <summary>How an environment applies its compose policy (ADR-0012).</summary>
public enum ComposePolicyMode
{
    /// <summary>No checks: compose files may do anything, as before the policy existed.</summary>
    Off,

    /// <summary>Violations are warnings in the deployment log; deployments go on.</summary>
    Audit,

    /// <summary>A deployment with a violation fails before compose runs.</summary>
    Enforce,
}

/// <summary>
/// What compose files of an environment's stacks may do on its host (ADR-0012): the mode, the stacks' data directory,
/// the host paths every stack may read, and the environment's allowlist. Administrators of the system or of the
/// environment change it. An environment without a stored policy has <see cref="Default"/>: off.
/// </summary>
public sealed class ComposePolicy
{
    public const string DefaultDataRoot = "/var/lib/container-forest/data";

    public const int MaxEntries = 100;

    /// <summary>Host files and directories every stack may mount read-only: time zone, locale, public CA certificates.</summary>
    public static readonly IReadOnlyList<string> DefaultSystemReadOnlyPaths =
    [
        "/etc/localtime",
        "/etc/timezone",
        "/usr/share/zoneinfo",
        "/etc/locale.conf",
        "/etc/default/locale",
        "/etc/os-release",
        "/etc/ssl/certs",
    ];

    /// <summary>
    /// Paths no allowlist opens, for reading as well as writing, together with what lies above and below them: the host's
    /// system, its users, Docker's state and the agent's own directories. Only a stack's allowance with the exact path does.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedPaths =
    [
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/root", "/run",
        "/sbin", "/sys", "/usr", "/var/run", "/var/lib/docker", "/var/lib/containerd", "/opt/container-forest",
    ];

    private List<string> systemReadOnlyPaths = [.. DefaultSystemReadOnlyPaths];
    private List<string> allowances = [];

    private ComposePolicy(EnvironmentId environmentId) => EnvironmentId = environmentId;

    // For EF Core.
    private ComposePolicy()
    {
        DataRoot = null!;
    }

    public EnvironmentId EnvironmentId { get; private init; }

    public ComposePolicyMode Mode { get; private set; }

    /// <summary>Parent of the stacks' data directories: each stack may mount &lt;DataRoot&gt;/&lt;stack&gt; read-write.</summary>
    public string DataRoot { get; private set; } = DefaultDataRoot;

    public IReadOnlyList<string> SystemReadOnlyPaths => systemReadOnlyPaths;

    /// <summary>Allowances for every stack of the environment, as <see cref="PolicyAllowance.Value"/>.</summary>
    public IReadOnlyList<string> Allowances => allowances;

    public IEnumerable<PolicyAllowance> ParsedAllowances => allowances.Select(PolicyAllowance.Parse);

    public bool IsOff => Mode == ComposePolicyMode.Off;

    public static ComposePolicy Default(EnvironmentId environmentId) => new(environmentId);

    /// <summary>The data directory of one stack: &lt;DataRoot&gt;/&lt;stack&gt;.</summary>
    public string DataDirectoryOf(string stackName) => DataRoot == "/" ? $"/{stackName}" : $"{DataRoot}/{stackName}";

    /// <exception cref="DomainValidationException">A path or an allowance is not valid, or there are too many.</exception>
    public void Change(ComposePolicyMode mode, string dataRoot, IEnumerable<string> systemPaths, IEnumerable<string> environmentAllowances)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new DomainValidationException($"Unknown compose policy mode {mode}.");
        }

        var root = HostPath.TryNormalize(dataRoot.Trim())
            ?? throw new DomainValidationException($"The data root '{dataRoot}' must be an absolute path without '.' or '..' segments.");
        if (ProtectedPaths.Any(p => p == root || (p != "/" && HostPath.IsAtOrUnder(root, p))))
        {
            throw new DomainValidationException($"The data root '{root}' must not lie in a protected host directory such as /etc or /opt/container-forest.");
        }

        var paths = Distinct(systemPaths.Select(p => HostPath.TryNormalize(p.Trim())
            ?? throw new DomainValidationException($"'{p}' is not an absolute path without '.' or '..' segments.")));
        if (paths.Contains("/"))
        {
            throw new DomainValidationException("The host's root cannot be a read-only system path for every stack.");
        }

        var parsed = Distinct(environmentAllowances.Select(a => PolicyAllowance.Parse(a).Value));
        if (paths.Count > MaxEntries || parsed.Count > MaxEntries)
        {
            throw new DomainValidationException($"A policy has at most {MaxEntries} system paths and {MaxEntries} allowances.");
        }

        Mode = mode;
        DataRoot = root;
        systemReadOnlyPaths = paths;
        allowances = parsed;
    }

    /// <summary>Validates and normalizes a stack's allowances (<see cref="Stacks.Stack.PolicyAllowances"/>).</summary>
    public static List<string> NormalizeAllowances(IEnumerable<string> values)
    {
        var parsed = Distinct(values.Select(a => PolicyAllowance.Parse(a).Value));
        return parsed.Count > MaxEntries
            ? throw new DomainValidationException($"A stack has at most {MaxEntries} allowances.")
            : parsed;
    }

    private static List<string> Distinct(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
