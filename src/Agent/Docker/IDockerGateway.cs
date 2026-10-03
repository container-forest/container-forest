using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Docker;

/// <summary>
/// The agent's view of the local Docker Engine. Failures are reported as <see cref="AgentCommandException"/>
/// subclasses, so command handlers do not depend on the Docker client library.
/// </summary>
public interface IDockerGateway
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>All containers, including stopped ones.</summary>
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken);

    /// <summary>One container by its full id, including a stopped one; null when it does not exist.</summary>
    Task<ContainerInfo?> FindContainerAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>
    /// Follows Docker's container events and passes each one to <paramref name="onEvent"/> in order, until cancelled
    /// or until the daemon ends the stream (it then returns).
    /// </summary>
    Task WatchContainerEventsAsync(Func<DockerContainerEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken);

    /// <summary>Succeeds when the container is already running.</summary>
    Task StartContainerAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Succeeds when the container is already stopped.</summary>
    /// <param name="stopTimeout">How long the process may shut down gracefully before Docker kills it.</param>
    Task StopContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    Task RestartContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    /// <summary>Reads the container's log once, without following it, and passes each line to <paramref name="onLine"/> in order.</summary>
    /// <param name="tailLines">How many of the last lines; 0 for all.</param>
    /// <param name="since">Only lines written at or after this moment; null for no lower bound.</param>
    /// <param name="onLine">Awaited before the next line is read, so a slow consumer slows the read down.</param>
    Task ReadContainerLogsAsync(
        string containerId,
        int tailLines,
        DateTimeOffset? since,
        Func<LogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken);
}

/// <param name="Action">Docker's action, e.g. "start", "die", "health_status: unhealthy".</param>
public sealed record DockerContainerEvent(string ContainerId, string Action, DateTimeOffset At);
