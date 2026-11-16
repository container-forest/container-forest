using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Metrics;

/// <summary>What metrics need from the Docker Engine. Failures are <see cref="Commands.AgentCommandException"/> subclasses.</summary>
public interface IDockerMetricsGateway
{
    Task<DockerRuntime> GetRuntimeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RunningContainer>> ListRunningContainersAsync(CancellationToken cancellationToken);

    /// <summary>The host pid of the container's main process; null when it is not running.</summary>
    Task<long?> GetPidAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>One reading of Docker's stats API, for hosts whose cgroup files the agent cannot read itself.</summary>
    Task<ContainerCounters?> ReadStatsAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>docker system df; null when the daemon answers in a format the client does not know.</summary>
    Task<DockerDiskUsage?> GetDiskUsageAsync(CancellationToken cancellationToken);
}

/// <param name="CgroupVersion">"1" or "2"; empty when Docker does not say.</param>
/// <param name="CgroupDriver">"systemd" or "cgroupfs".</param>
public sealed record DockerRuntime(string CgroupVersion, string CgroupDriver);

public sealed record RunningContainer(string Id, string Name, string StackName, string ServiceName, bool HostNetwork);
