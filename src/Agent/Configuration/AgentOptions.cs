namespace ContainerForest.Agent.Configuration;

/// <summary>Configuration section "Agent" (env: CF_AGENT__...).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>The aggregator's gRPC endpoint, e.g. https://forest.example.com or http://localhost:5081.</summary>
    public Uri? ControllerUrl { get; set; }

    /// <summary>One-time token; needed only while the agent has no saved credentials.</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Directory for the agent's own data, including its credentials.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary>
    /// Parent of the stack directories (&lt;StacksDirectory&gt;/&lt;stack&gt;); "stacks" in <see cref="DataDirectory"/> when not set.
    /// It must be mounted into the agent's container at the same path as on the host: the Docker daemon resolves
    /// bind mounts of the stacks by host paths.
    /// </summary>
    public string? StacksDirectory { get; set; }

    /// <summary>
    /// Host tmpfs for secret files, so their content never reaches a disk. Not used on Windows (development only),
    /// where secret files are written into the stack directory.
    /// </summary>
    public string SecretsDirectory { get; set; } = "/run/container-forest";

    /// <summary>
    /// The host's root directory mounted read-only into the agent's container (deploy/agent/compose.yaml: /:/host:ro,rslave),
    /// so the agent can check bind-mount sources anywhere on the host for symbolic links (ADR-0012). Null when not
    /// mounted: then only paths in the agent's own directories, which are mounted at the same path, can be checked.
    /// </summary>
    public string? HostRoot { get; set; }

    /// <summary>
    /// Directories with compose projects the host's administrator runs by hand, e.g. /srv/docker: the panel can then
    /// pull, up, restart and take them down as unmanaged stacks (ADR-0016). Each must be mounted into the agent's
    /// container at the same path as on the host, read-only is enough, because compose resolves the projects' relative
    /// paths there. Empty (the default) turns unmanaged stacks off. They must not overlap the agent's own directories.
    /// </summary>
    public List<string> UnmanagedStackRoots { get; set; } = [];

    /// <summary>The docker CLI with the compose plugin; a name is looked up in PATH.</summary>
    public string DockerExecutable { get; set; } = "docker";

    /// <summary>How long one docker compose run may take. The aggregator waits up to 15 minutes for a deployment.</summary>
    public TimeSpan ComposeTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The aggregator drops a connection that is silent for 45 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the agent sends resource usage (ADR-0018); zero turns metrics off. Container counters come from the
    /// host's cgroup files under <see cref="HostRoot"/>, or from Docker's stats API where those cannot be read.
    /// </summary>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the agent asks Docker for its disk usage (docker system df), which is slow on big hosts.</summary>
    public TimeSpan DiskUsageInterval { get; set; } = TimeSpan.FromMinutes(15);

    public string StacksRoot => StacksDirectory ?? Path.Combine(DataDirectory, "stacks");

    public static string DefaultDataDirectory() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "container-forest")
            : "/opt/container-forest";

    /// <summary>The unmanaged stack roots as full paths without a trailing separator.</summary>
    public IReadOnlyList<string> NormalizedUnmanagedStackRoots =>
        UnmanagedStackRoots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct().ToList();

    // An absolute path that neither contains nor lies in a directory the agent writes itself: a stack the agent
    // deploys must never be taken for an unmanaged one, and the agent's credentials must stay out of compose's reach.
    private bool IsValidUnmanagedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string[] own = [DataDirectory, StacksRoot, SecretsDirectory];
        return own.Where(d => Path.IsPathFullyQualified(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .All(d => !PathContains(full, d) && !PathContains(d, full));
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or lies in it, compared by whole segments.</summary>
    public static bool PathContains(string directory, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(directory);
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || (root.EndsWith(Path.DirectorySeparatorChar) && path.StartsWith(root, comparison));
    }

    public bool IsValid() =>
        ControllerUrl is { IsAbsoluteUri: true } url
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(DataDirectory)
        && Path.IsPathFullyQualified(StacksRoot)
        && (OperatingSystem.IsWindows() || Path.IsPathFullyQualified(SecretsDirectory))
        && (HostRoot is null || Path.IsPathFullyQualified(HostRoot))
        && UnmanagedStackRoots.All(IsValidUnmanagedRoot)
        && !string.IsNullOrWhiteSpace(DockerExecutable)
        && ComposeTimeout > TimeSpan.Zero
        && HeartbeatInterval > TimeSpan.Zero
        && (MetricsInterval == TimeSpan.Zero || MetricsInterval >= TimeSpan.FromSeconds(1))
        && DiskUsageInterval >= TimeSpan.FromMinutes(1);
}
