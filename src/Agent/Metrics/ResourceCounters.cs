namespace ContainerForest.Agent.Metrics;

/// <summary>
/// A container's resource usage as read at one moment: cumulative counters (CPU time, bytes) that only grow while the
/// container runs, and gauges. <see cref="MetricsRates"/> turns two readings into rates.
/// </summary>
public sealed record ContainerCounters
{
    public ulong CpuUsageNanoseconds { get; init; }
    public ulong CpuPeriods { get; init; }
    public ulong CpuThrottledPeriods { get; init; }

    /// <summary>0 = no limit.</summary>
    public double CpuLimitCores { get; init; }

    public ulong MemoryWorkingSetBytes { get; init; }
    public ulong MemoryCacheBytes { get; init; }

    /// <summary>0 = no limit.</summary>
    public ulong MemoryLimitBytes { get; init; }

    /// <summary>Null when unknown: host networking, or the container's network namespace could not be read.</summary>
    public NetworkCounters? Network { get; init; }

    public ulong BlockReadBytes { get; init; }
    public ulong BlockWriteBytes { get; init; }
    public uint Pids { get; init; }
}

public readonly record struct NetworkCounters(ulong RxBytes, ulong TxBytes);

/// <summary>The host's /proc counters; CPU times are in clock ticks, which cancel out in ratios.</summary>
public sealed record HostCounters
{
    public uint CpuCount { get; init; }
    public CpuTimes Cpu { get; init; }
    public double Load1 { get; init; }
    public double Load5 { get; init; }
    public double Load15 { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public ulong MemoryAvailableBytes { get; init; }
    public ulong SwapTotalBytes { get; init; }
    public ulong SwapFreeBytes { get; init; }
    public long UptimeSeconds { get; init; }
    public IReadOnlyList<FilesystemUsage> Filesystems { get; init; } = [];
    public IReadOnlyDictionary<string, NetworkCounters> Interfaces { get; init; } = new Dictionary<string, NetworkCounters>();
    public ulong DiskReadBytes { get; init; }
    public ulong DiskWriteBytes { get; init; }
}

/// <summary>The "cpu" line of /proc/stat.</summary>
public readonly record struct CpuTimes(ulong User, ulong System, ulong Idle, ulong IoWait, ulong Steal)
{
    public ulong Total => User + System + Idle + IoWait + Steal;
}

public sealed record FilesystemUsage(string MountPoint, string Device, string Type, ulong TotalBytes, ulong AvailableBytes, ulong UsedBytes);
