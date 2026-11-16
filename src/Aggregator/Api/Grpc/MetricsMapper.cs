using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Grpc;

/// <summary>Converts the agent's metrics messages into the application's samples.</summary>
public static class MetricsMapper
{
    /// <summary>The longest names kept: the agent is trusted with its host, not with the aggregator's memory.</summary>
    private const int MaxNameLength = 256;
    private const int MaxContainers = 2000;
    private const int MaxListItems = 256;

    public static MetricsSourceKind ToSource(MetricsSource source) => source switch
    {
        MetricsSource.CgroupV2 => MetricsSourceKind.CgroupV2,
        MetricsSource.DockerStats => MetricsSourceKind.DockerStats,
        MetricsSource.Disabled => MetricsSourceKind.Disabled,
        _ => MetricsSourceKind.Unsupported,
    };

    /// <param name="receivedAt">Used when the agent's clock is far off: graphs follow the aggregator's time.</param>
    public static MetricsSnapshot ToSnapshot(EnvironmentId environmentId, MetricsSample sample, DateTimeOffset receivedAt)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(sample.TimestampUnixMs);
        if ((at - receivedAt).Duration() > TimeSpan.FromMinutes(1))
        {
            at = receivedAt;
        }

        return new MetricsSnapshot(
            environmentId,
            at,
            sample.Host is { } host ? ToHost(host) : null,
            sample.Containers.Take(MaxContainers).Select(ToContainer).ToList(),
            sample.DiskUsage is { } usage ? ToDiskUsage(usage, at) : null);
    }

    private static HostSample ToHost(HostMetrics h) => new(
        h.HasRates,
        h.CpuCount,
        Ratio(h.CpuUserRatio),
        Ratio(h.CpuSystemRatio),
        Ratio(h.CpuIowaitRatio),
        Ratio(h.CpuStealRatio),
        Finite(h.Load1),
        Finite(h.Load5),
        Finite(h.Load15),
        h.MemoryTotalBytes,
        h.MemoryAvailableBytes,
        h.SwapTotalBytes,
        h.SwapFreeBytes,
        Finite(h.DiskReadBytesPerSecond),
        Finite(h.DiskWriteBytesPerSecond),
        h.Filesystems.Take(MaxListItems)
            .Select(f => new FilesystemSample(Name(f.MountPoint), Name(f.Device), Name(f.Type), f.TotalBytes, f.AvailableBytes, f.UsedBytes))
            .ToList(),
        h.Interfaces.Take(MaxListItems)
            .Select(i => new InterfaceSample(Name(i.Name), Finite(i.RxBytesPerSecond), Finite(i.TxBytesPerSecond)))
            .ToList(),
        h.UptimeSeconds);

    private static ContainerSample ToContainer(ContainerMetrics c) => new(
        Name(c.ContainerId),
        Name(c.Name),
        c.StackName is { Length: > 0 } stack ? Name(stack) : null,
        c.ServiceName is { Length: > 0 } service ? Name(service) : null,
        c.HasRates,
        Finite(c.CpuCores),
        Finite(c.CpuLimitCores),
        Ratio(c.CpuThrottledRatio),
        c.MemoryWorkingSetBytes,
        c.MemoryCacheBytes,
        c.MemoryLimitBytes,
        Finite(c.NetworkRxBytesPerSecond),
        Finite(c.NetworkTxBytesPerSecond),
        c.HostNetwork,
        Finite(c.BlockReadBytesPerSecond),
        Finite(c.BlockWriteBytesPerSecond),
        c.Pids);

    private static DiskUsageSample ToDiskUsage(DockerDiskUsage d, DateTimeOffset at) => new(
        at,
        d.ImagesBytes,
        d.ImagesReclaimableBytes,
        d.ContainersBytes,
        d.VolumesBytes,
        d.BuildCacheBytes,
        d.Containers.Take(MaxContainers).Select(c => new ContainerLayerUsage(Name(c.ContainerId), Name(c.Name), c.WritableLayerBytes)).ToList(),
        d.Volumes.Take(MaxContainers).Select(v => new VolumeUsage(Name(v.Name), v.StackName is { Length: > 0 } s ? Name(s) : null, v.SizeBytes)).ToList());

    private static string Name(string value) => value.Length > MaxNameLength ? value[..MaxNameLength] : value;

    private static double Finite(double value) => double.IsFinite(value) && value >= 0 ? value : 0;

    private static double Ratio(double value) => Math.Clamp(Finite(value), 0, 1);
}
