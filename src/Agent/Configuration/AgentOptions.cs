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

    /// <summary>The docker CLI with the compose plugin; a name is looked up in PATH.</summary>
    public string DockerExecutable { get; set; } = "docker";

    /// <summary>How long one docker compose run may take. The aggregator waits up to 15 minutes for a deployment.</summary>
    public TimeSpan ComposeTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The aggregator drops a connection that is silent for 45 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public string StacksRoot => StacksDirectory ?? Path.Combine(DataDirectory, "stacks");

    public static string DefaultDataDirectory() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "container-forest")
            : "/opt/container-forest";

    internal bool IsValid() =>
        ControllerUrl is { IsAbsoluteUri: true } url
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(DataDirectory)
        && Path.IsPathFullyQualified(StacksRoot)
        && (OperatingSystem.IsWindows() || Path.IsPathFullyQualified(SecretsDirectory))
        && (HostRoot is null || Path.IsPathFullyQualified(HostRoot))
        && !string.IsNullOrWhiteSpace(DockerExecutable)
        && ComposeTimeout > TimeSpan.Zero
        && HeartbeatInterval > TimeSpan.Zero;
}
