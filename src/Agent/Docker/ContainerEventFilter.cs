namespace ContainerForest.Agent.Docker;

/// <summary>
/// Which Docker container events reach the aggregator: those that change what a container is or how it runs.
/// Exec, attach, resize, copy and the like are left out. Besides being noise, an exec action carries the command
/// line ("exec_start: sh -c …"), which may hold a secret.
/// </summary>
public static class ContainerEventFilter
{
    private static readonly HashSet<string> Relevant =
        ["create", "start", "restart", "stop", "die", "pause", "unpause", "destroy", "rename", "oom", "update"];

    private const string HealthPrefix = "health_status";

    public static bool IsRelevant(string action) =>
        Relevant.Contains(action) || action.StartsWith(HealthPrefix, StringComparison.Ordinal);

    /// <summary>True when the container no longer exists after the event, so there is nothing to look up.</summary>
    public static bool IsRemoval(string action) => action == "destroy";
}
