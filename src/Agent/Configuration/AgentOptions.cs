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

    /// <summary>The aggregator drops a connection that is silent for 45 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public static string DefaultDataDirectory() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "container-forest")
            : "/opt/container-forest";

    internal bool IsValid() =>
        ControllerUrl is { IsAbsoluteUri: true } url
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(DataDirectory)
        && HeartbeatInterval > TimeSpan.Zero;
}
