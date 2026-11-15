using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Metrics;
using ContainerForest.Contracts.Agent;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Docker;

public sealed partial class DockerDotNetGateway
{
    /// <summary>docker system df walks every layer and volume: give it more time than ordinary calls.</summary>
    public static readonly TimeSpan DiskUsageTimeout = TimeSpan.FromMinutes(2);

    public Task<DockerRuntime> GetRuntimeAsync(CancellationToken cancellationToken) =>
        CallAsync(
            null,
            RequestTimeout,
            async ct =>
            {
                var info = await client.System.GetSystemInfoAsync(ct);
                return new DockerRuntime(info.CgroupVersion ?? "", info.CgroupDriver ?? "");
            },
            cancellationToken);

    public Task<IReadOnlyList<RunningContainer>> ListRunningContainersAsync(CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<RunningContainer>>(
            null,
            RequestTimeout,
            async ct =>
            {
                var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = false }, ct);
                return containers
                    .Select(c => (Info: ContainerInfoMapper.ToContainerInfo(c), c.HostConfig?.NetworkMode))
                    .Where(c => c.Info.Id.Length > 0)
                    .Select(c => new RunningContainer(
                        c.Info.Id, c.Info.Name, c.Info.StackName, c.Info.ServiceName, c.NetworkMode == "host"))
                    .ToList();
            },
            cancellationToken);

    public Task<long?> GetPidAsync(string containerId, CancellationToken cancellationToken) =>
        CallAsync<long?>(
            containerId,
            RequestTimeout,
            async ct =>
            {
                ContainerIds.Validate(containerId);
                var inspect = await client.Containers.InspectContainerAsync(containerId, ct);
                return inspect.State is { Running: true, Pid: > 0 } state ? state.Pid : null;
            },
            cancellationToken);

    public Task<ContainerCounters?> ReadStatsAsync(string containerId, CancellationToken cancellationToken) =>
        CallAsync(
            containerId,
            RequestTimeout,
            async ct =>
            {
                ContainerIds.Validate(containerId);
                ContainerStatsResponse? stats = null;
                await client.Containers.GetContainerStatsAsync(
                    containerId,
                    new ContainerStatsParameters { Stream = false, OneShot = true },
                    new LastValue<ContainerStatsResponse>(s => stats = s),
                    ct);
                return stats is null ? null : DockerStatsMapper.ToCounters(stats);
            },
            cancellationToken);

    public Task<DockerDiskUsage?> GetDiskUsageAsync(CancellationToken cancellationToken) =>
        CallAsync(
            null,
            DiskUsageTimeout,
            async ct =>
            {
                var usage = await client.System.GetDataUsageInfoAsync(new SytemDataUsageInfoParameters { Verbose = true }, ct);
                return DockerStatsMapper.ToDiskUsage(usage);
            },
            cancellationToken);

    // Progress<T> reports on the thread pool, possibly after the call returned; this one reports in line.
    private sealed class LastValue<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
