namespace ContainerForest.Agent.Metrics;

/// <summary>
/// The host's /proc, /sys and cgroup files, under the host root mounted into the agent's container
/// (Agent:HostRoot, "/host" in deploy/agent/compose.yaml) or under "/" when the agent runs on the host itself.
/// /proc/self/... would describe the agent's own namespaces, so host-wide files are read through pid 1.
/// </summary>
public sealed class HostFiles(string root)
{
    public string Root { get; } = root;

    public string PathOf(string relative) => Path.Combine(Root, relative.TrimStart('/'));

    /// <summary>The file's content; null when it does not exist or cannot be read (a process that just exited).</summary>
    public string? ReadText(string relative)
    {
        try
        {
            return File.ReadAllText(PathOf(relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Exists(string relative) => Path.Exists(PathOf(relative));
}
