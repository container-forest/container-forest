using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>How an environment's agent reads resource usage, from its Hello.</summary>
public enum MetricsSourceKind
{
    /// <summary>An agent without metrics.</summary>
    Unsupported,
    CgroupV2,
    DockerStats,
    Disabled,
}

/// <summary>One sample an agent sent: the host and its running containers at one moment.</summary>
/// <param name="Host">Null when the agent cannot read the host's /proc (not Linux, or the host root is not mounted).</param>
public sealed record MetricsSnapshot(
    EnvironmentId EnvironmentId,
    DateTimeOffset At,
    HostSample? Host,
    IReadOnlyList<ContainerSample> Containers,
    DiskUsageSample? DiskUsage = null);

/// <param name="HasRates">False for the first sample after a connect or a reboot: CPU, disk and network are unknown.</param>
public sealed record HostSample(
    bool HasRates,
    uint CpuCount,
    double CpuUser,
    double CpuSystem,
    double CpuIowait,
    double CpuSteal,
    double Load1,
    double Load5,
    double Load15,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
    ulong SwapTotalBytes,
    ulong SwapFreeBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    IReadOnlyList<FilesystemSample> Filesystems,
    IReadOnlyList<InterfaceSample> Interfaces,
    long UptimeSeconds)
{
    /// <summary>Busy share of all cores, 0..1.</summary>
    public double CpuBusy => CpuUser + CpuSystem + CpuIowait + CpuSteal;

    public ulong MemoryUsedBytes => MemoryTotalBytes > MemoryAvailableBytes ? MemoryTotalBytes - MemoryAvailableBytes : 0;

    public ulong SwapUsedBytes => SwapTotalBytes > SwapFreeBytes ? SwapTotalBytes - SwapFreeBytes : 0;

    public double NetworkRxBytesPerSecond => Interfaces.Sum(i => i.RxBytesPerSecond);

    public double NetworkTxBytesPerSecond => Interfaces.Sum(i => i.TxBytesPerSecond);
}

public sealed record FilesystemSample(string MountPoint, string Device, string Type, ulong TotalBytes, ulong AvailableBytes, ulong UsedBytes)
{
    /// <summary>As df shows it: used against what is usable, without the blocks reserved for root.</summary>
    public double UsedRatio => UsedBytes + AvailableBytes > 0 ? (double)UsedBytes / (UsedBytes + AvailableBytes) : 0;
}

public sealed record InterfaceSample(string Name, double RxBytesPerSecond, double TxBytesPerSecond);

/// <param name="StackName">The compose project; null outside compose.</param>
/// <param name="MemoryLimitBytes">0 = no limit.</param>
/// <param name="CpuLimitCores">0 = no limit.</param>
public sealed record ContainerSample(
    string Id,
    string Name,
    string? StackName,
    string? ServiceName,
    bool HasRates,
    double CpuCores,
    double CpuLimitCores,
    double CpuThrottledRatio,
    ulong MemoryWorkingSetBytes,
    ulong MemoryCacheBytes,
    ulong MemoryLimitBytes,
    double NetworkRxBytesPerSecond,
    double NetworkTxBytesPerSecond,
    bool HostNetwork,
    double BlockReadBytesPerSecond,
    double BlockWriteBytesPerSecond,
    uint Pids);

/// <summary>docker system df of an environment.</summary>
public sealed record DiskUsageSample(
    DateTimeOffset At,
    ulong ImagesBytes,
    ulong ImagesReclaimableBytes,
    ulong ContainersBytes,
    ulong VolumesBytes,
    ulong BuildCacheBytes,
    IReadOnlyList<ContainerLayerUsage> Containers,
    IReadOnlyList<VolumeUsage> Volumes);

public sealed record ContainerLayerUsage(string ContainerId, string Name, ulong WritableLayerBytes);

/// <param name="StackName">The compose project of the volume; null for other volumes.</param>
public sealed record VolumeUsage(string Name, string? StackName, ulong SizeBytes);

/// <summary>Where stored points come from, finest first.</summary>
public enum MetricsResolution
{
    /// <summary>The samples as the agent sent them, kept in memory for the last hour.</summary>
    Raw,
    Minute,
    Hour,
}

/// <summary>
/// A container's usage over one stored period (a minute, an hour) or one bucket of a query: averages, and maxima of
/// the averages of shorter periods. <see cref="Start"/> is in Unix seconds.
/// </summary>
public sealed record ContainerPoint(
    EnvironmentId EnvironmentId,
    string Container,
    long Start,
    string Stack,
    string Service,
    double CpuAvg,
    double CpuMax,
    double CpuLimit,
    double ThrottledAvg,
    double MemoryAvg,
    double MemoryMax,
    double MemoryLimit,
    double CacheAvg,
    double RxAvg,
    double TxAvg,
    double ReadAvg,
    double WriteAvg,
    double PidsMax);

/// <summary>The host's usage over one period. CPU values are shares of all cores, 0..1.</summary>
public sealed record HostPoint(
    EnvironmentId EnvironmentId,
    long Start,
    double CpuCount,
    double CpuUser,
    double CpuSystem,
    double CpuIowait,
    double CpuSteal,
    double CpuBusyMax,
    double Load1,
    double MemoryUsedAvg,
    double MemoryUsedMax,
    double MemoryTotal,
    double SwapUsed,
    double SwapTotal,
    double DiskRead,
    double DiskWrite,
    double NetworkRx,
    double NetworkTx);

public sealed record FilesystemPoint(EnvironmentId EnvironmentId, string MountPoint, long Start, double Used, double Total);

/// <summary>One finished minute of an environment, ready to be stored.</summary>
public sealed record MinuteBatch(HostPoint? Host, IReadOnlyList<ContainerPoint> Containers, IReadOnlyList<FilesystemPoint> Filesystems);
