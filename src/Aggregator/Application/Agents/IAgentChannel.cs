using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Agents;

/// <summary>
/// Sends commands to connected agents. Implemented over the gRPC stream in the infrastructure layer.
/// </summary>
/// <remarks>
/// Commands are never queued: when the agent is offline the call fails at once with
/// <see cref="AgentOfflineException"/> (security invariant 7).
/// </remarks>
public interface IAgentChannel
{
    bool IsOnline(EnvironmentId environmentId);

    Task<IReadOnlyList<ContainerSnapshot>> ListContainersAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    Task StartContainerAsync(EnvironmentId environmentId, string containerId, CancellationToken cancellationToken);

    /// <param name="stopTimeout">How long Docker waits for the process to exit before killing it.</param>
    Task StopContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    /// <param name="stopTimeout">How long Docker waits for the process to exit before killing it.</param>
    Task RestartContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);
}

/// <summary>Live state of a container as the agent reports it; not stored in the database.</summary>
public sealed record ContainerSnapshot(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    DateTimeOffset? CreatedAt);

public enum ContainerState
{
    Unknown,
    Created,
    Running,
    Paused,
    Restarting,
    Removing,
    Exited,
    Dead,
}
