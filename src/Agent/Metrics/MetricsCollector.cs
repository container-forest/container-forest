using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Metrics;

/// <summary>
/// Takes one <see cref="MetricsSample"/> at a time: lists the running containers, reads their counters from the cgroup
/// files (or Docker's stats API where those are out of reach) and the host's from /proc, and keeps the previous
/// readings to turn counters into rates. Not thread-safe: <see cref="MetricsForwarder"/> calls it from one loop.
/// </summary>
public sealed partial class MetricsCollector
{
    private const int DockerStatsParallelism = 4;

    private readonly IDockerMetricsGateway docker;
    private readonly AgentOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<MetricsCollector> logger;
    private readonly CgroupV2Reader cgroups;
    private readonly HostMetricsReader host;
    private readonly Dictionary<string, Tracked> tracked = new(StringComparer.Ordinal);
    private (HostCounters Counters, DateTimeOffset At)? previousHost;
    private DateTimeOffset? previousSampleAt;

    public MetricsCollector(IDockerMetricsGateway docker, IOptions<AgentOptions> options, TimeProvider time, ILogger<MetricsCollector> logger)
    {
        this.docker = docker;
        this.options = options.Value;
        this.time = time;
        this.logger = logger;
        var files = new HostFiles(this.options.HostRoot ?? "/");
        cgroups = new CgroupV2Reader(files);
        host = new HostMetricsReader(files);
    }

    public MetricsSource Source { get; private set; } = MetricsSource.Unspecified;

    /// <summary>Chooses where container counters come from; called before each session's Hello.</summary>
    public async Task<MetricsSource> DetectSourceAsync(CancellationToken cancellationToken)
    {
        if (options.MetricsInterval <= TimeSpan.Zero)
        {
            return Source = MetricsSource.Disabled;
        }

        var cgroupVersion = "";
        try
        {
            cgroupVersion = (await docker.GetRuntimeAsync(cancellationToken)).CgroupVersion;
        }
        catch (AgentCommandException ex)
        {
            LogRuntimeUnknown(ex.Message);
        }

        var source = OperatingSystem.IsLinux() && cgroupVersion is "2" or "" && cgroups.IsAvailable
            ? MetricsSource.CgroupV2
            : MetricsSource.DockerStats;
        if (source != Source)
        {
            LogSource(source, options.HostRoot ?? "/");
        }

        return Source = source;
    }

    public async Task<MetricsSample> CollectAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sample = new MetricsSample
        {
            TimestampUnixMs = now.ToUnixTimeMilliseconds(),
            IntervalMs = previousSampleAt is { } before ? (long)(now - before).TotalMilliseconds : 0,
        };
        previousSampleAt = now;

        var hostMemory = 0UL;
        if (host.Read() is { } hostCounters)
        {
            sample.Host = MetricsRates.ToHostMetrics(hostCounters, previousHost?.Counters, previousHost is { } p ? now - p.At : TimeSpan.Zero);
            previousHost = (hostCounters, now);
            hostMemory = hostCounters.MemoryTotalBytes;
        }

        var containers = await docker.ListRunningContainersAsync(cancellationToken);
        var readings = Source == MetricsSource.CgroupV2
            ? await ReadFromCgroupsAsync(containers, cancellationToken)
            : await ReadFromDockerAsync(containers, cancellationToken);

        foreach (var (container, counters) in readings)
        {
            var previous = tracked.GetValueOrDefault(container.Id);
            sample.Containers.Add(MetricsRates.ToContainerMetrics(
                container, counters, previous?.Counters, previous is null ? TimeSpan.Zero : now - previous.At, hostMemory));
            tracked[container.Id] = (previous ?? new Tracked()) with { Counters = counters, At = now };
        }

        var running = containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in tracked.Keys.Where(id => !running.Contains(id)).ToList())
        {
            tracked.Remove(gone);
        }

        return sample;
    }

    public Task<DockerDiskUsage?> GetDiskUsageAsync(CancellationToken cancellationToken) => docker.GetDiskUsageAsync(cancellationToken);

    private async Task<List<(RunningContainer, ContainerCounters)>> ReadFromCgroupsAsync(
        IReadOnlyList<RunningContainer> containers, CancellationToken cancellationToken)
    {
        var result = new List<(RunningContainer, ContainerCounters)>();
        foreach (var container in containers)
        {
            var entry = tracked.GetValueOrDefault(container.Id) ?? new Tracked();
            var pid = entry.Pid;
            if (pid is null || entry.Directory is null || !cgroups.BelongsTo(pid.Value, entry.Directory))
            {
                pid = await TryGetPidAsync(container.Id, cancellationToken);
                entry = entry with { Pid = pid, Directory = cgroups.FindDirectory(container.Id, pid) };
                tracked[container.Id] = entry;
            }

            var counters = entry.Directory is { } directory ? cgroups.Read(directory) : null;
            if (counters is null)
            {
                // A cgroup_parent the agent cannot see, or a container that stopped since it was listed.
                counters = await TryReadStatsAsync(container.Id, cancellationToken);
            }
            else if (!container.HostNetwork && pid is { } p)
            {
                counters = counters with { Network = cgroups.ReadNetwork(p) };
            }

            if (counters is not null)
            {
                result.Add((container, counters));
            }
        }

        return result;
    }

    private async Task<List<(RunningContainer, ContainerCounters)>> ReadFromDockerAsync(
        IReadOnlyList<RunningContainer> containers, CancellationToken cancellationToken)
    {
        var readings = new (RunningContainer, ContainerCounters?)[containers.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, containers.Count),
            new ParallelOptions { MaxDegreeOfParallelism = DockerStatsParallelism, CancellationToken = cancellationToken },
            async (i, ct) => readings[i] = (containers[i], await TryReadStatsAsync(containers[i].Id, ct)));
        return readings.Where(r => r.Item2 is not null).Select(r => (r.Item1, r.Item2!)).ToList();
    }

    private async Task<long?> TryGetPidAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            return await docker.GetPidAsync(containerId, cancellationToken);
        }
        catch (AgentCommandException ex)
        {
            LogReadFailed(containerId, ex.Message);
            return null;
        }
    }

    private async Task<ContainerCounters?> TryReadStatsAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            return await docker.ReadStatsAsync(containerId, cancellationToken);
        }
        catch (AgentCommandException ex)
        {
            LogReadFailed(containerId, ex.Message);
            return null;
        }
    }

    private sealed record Tracked
    {
        public long? Pid { get; init; }
        public string? Directory { get; init; }
        public ContainerCounters? Counters { get; init; }
        public DateTimeOffset At { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading resource usage with {Source} (host root {HostRoot})")]
    private partial void LogSource(MetricsSource source, string hostRoot);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Docker did not report its cgroup version: {Reason}")]
    private partial void LogRuntimeUnknown(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resource usage of container {ContainerId} could not be read: {Reason}")]
    private partial void LogReadFailed(string containerId, string reason);
}
