using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Metrics;

/// <summary>
/// Turns two readings of cumulative counters into the protocol's rates. A counter that went down means the container
/// restarted (or a cgroup was recreated) in between: the sample then has no rates rather than a negative spike.
/// </summary>
public static class MetricsRates
{
    public static ContainerMetrics ToContainerMetrics(
        RunningContainer container, ContainerCounters current, ContainerCounters? previous, TimeSpan elapsed, ulong hostMemoryBytes)
    {
        var metrics = new ContainerMetrics
        {
            ContainerId = container.Id,
            Name = container.Name,
            StackName = container.StackName,
            ServiceName = container.ServiceName,
            CpuLimitCores = current.CpuLimitCores,
            MemoryWorkingSetBytes = current.MemoryWorkingSetBytes,
            MemoryCacheBytes = current.MemoryCacheBytes,
            // Docker stats reports the host's memory when there is no limit, cgroup files a huge number.
            MemoryLimitBytes = hostMemoryBytes > 0 && current.MemoryLimitBytes >= hostMemoryBytes ? 0 : current.MemoryLimitBytes,
            HostNetwork = container.HostNetwork,
            Pids = current.Pids,
        };

        if (previous is null || elapsed <= TimeSpan.Zero || !Grew(previous, current))
        {
            return metrics;
        }

        var seconds = elapsed.TotalSeconds;
        metrics.HasRates = true;
        metrics.CpuCores = (current.CpuUsageNanoseconds - previous.CpuUsageNanoseconds) / 1e9 / seconds;
        var periods = current.CpuPeriods - previous.CpuPeriods;
        metrics.CpuThrottledRatio = periods > 0 ? Math.Clamp((double)(current.CpuThrottledPeriods - previous.CpuThrottledPeriods) / periods, 0, 1) : 0;
        metrics.BlockReadBytesPerSecond = (current.BlockReadBytes - previous.BlockReadBytes) / seconds;
        metrics.BlockWriteBytesPerSecond = (current.BlockWriteBytes - previous.BlockWriteBytes) / seconds;
        if (!container.HostNetwork && current.Network is { } now && previous.Network is { } before
            && now.RxBytes >= before.RxBytes && now.TxBytes >= before.TxBytes)
        {
            metrics.NetworkRxBytesPerSecond = (now.RxBytes - before.RxBytes) / seconds;
            metrics.NetworkTxBytesPerSecond = (now.TxBytes - before.TxBytes) / seconds;
        }

        return metrics;
    }

    public static HostMetrics ToHostMetrics(HostCounters current, HostCounters? previous, TimeSpan elapsed)
    {
        var metrics = new HostMetrics
        {
            CpuCount = current.CpuCount,
            Load1 = current.Load1,
            Load5 = current.Load5,
            Load15 = current.Load15,
            MemoryTotalBytes = current.MemoryTotalBytes,
            MemoryAvailableBytes = current.MemoryAvailableBytes,
            SwapTotalBytes = current.SwapTotalBytes,
            SwapFreeBytes = current.SwapFreeBytes,
            UptimeSeconds = current.UptimeSeconds,
        };
        metrics.Filesystems.AddRange(current.Filesystems.Select(f => new FilesystemMetrics
        {
            MountPoint = f.MountPoint,
            Device = f.Device,
            Type = f.Type,
            TotalBytes = f.TotalBytes,
            AvailableBytes = f.AvailableBytes,
            UsedBytes = f.UsedBytes,
        }));

        var seconds = elapsed.TotalSeconds;
        var rated = previous is not null && seconds > 0;
        foreach (var (name, now) in current.Interfaces.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            var item = new NetworkInterfaceMetrics { Name = name };
            if (rated && previous!.Interfaces.TryGetValue(name, out var before) && now.RxBytes >= before.RxBytes && now.TxBytes >= before.TxBytes)
            {
                item.RxBytesPerSecond = (now.RxBytes - before.RxBytes) / seconds;
                item.TxBytesPerSecond = (now.TxBytes - before.TxBytes) / seconds;
            }

            metrics.Interfaces.Add(item);
        }

        if (!rated || current.Cpu.Total <= previous!.Cpu.Total || current.UptimeSeconds < previous.UptimeSeconds)
        {
            return metrics;
        }

        var total = (double)(current.Cpu.Total - previous.Cpu.Total);
        metrics.HasRates = true;
        metrics.CpuUserRatio = Delta(current.Cpu.User, previous.Cpu.User) / total;
        metrics.CpuSystemRatio = Delta(current.Cpu.System, previous.Cpu.System) / total;
        metrics.CpuIowaitRatio = Delta(current.Cpu.IoWait, previous.Cpu.IoWait) / total;
        metrics.CpuStealRatio = Delta(current.Cpu.Steal, previous.Cpu.Steal) / total;
        metrics.DiskReadBytesPerSecond = Delta(current.DiskReadBytes, previous.DiskReadBytes) / seconds;
        metrics.DiskWriteBytesPerSecond = Delta(current.DiskWriteBytes, previous.DiskWriteBytes) / seconds;
        return metrics;
    }

    private static bool Grew(ContainerCounters previous, ContainerCounters current) =>
        current.CpuUsageNanoseconds >= previous.CpuUsageNanoseconds
        && current.CpuPeriods >= previous.CpuPeriods
        && current.CpuThrottledPeriods >= previous.CpuThrottledPeriods
        && current.BlockReadBytes >= previous.BlockReadBytes
        && current.BlockWriteBytes >= previous.BlockWriteBytes;

    // iowait can go backwards on some kernels (proc(5)); a dip counts as no time rather than wrapping around.
    private static double Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;
}
