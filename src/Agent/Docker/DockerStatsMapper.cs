using ContainerForest.Agent.Metrics;
using ContainerForest.Contracts.Agent;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Docker;

/// <summary>Converts Docker's stats and disk usage answers into the agent's counters and the protocol's messages.</summary>
public static class DockerStatsMapper
{
    public static ContainerCounters ToCounters(ContainerStatsResponse stats)
    {
        var memory = stats.MemoryStats;
        var memoryStats = memory?.Stats ?? new Dictionary<string, ulong>();
        var usage = memory?.Usage ?? 0;
        // cgroup v2 names the inactive page cache "inactive_file", v1 "total_inactive_file"; docker stats subtracts it too.
        var inactive = Stat(memoryStats, "inactive_file") ?? Stat(memoryStats, "total_inactive_file") ?? 0;
        var cache = Stat(memoryStats, "file") ?? Stat(memoryStats, "cache") ?? 0;

        var blkio = stats.BlkioStats?.IoServiceBytesRecursive ?? [];
        return new ContainerCounters
        {
            CpuUsageNanoseconds = stats.CPUStats?.CPUUsage?.TotalUsage ?? 0,
            CpuPeriods = stats.CPUStats?.ThrottlingData?.Periods ?? 0,
            CpuThrottledPeriods = stats.CPUStats?.ThrottlingData?.ThrottledPeriods ?? 0,
            MemoryWorkingSetBytes = usage > inactive ? usage - inactive : 0,
            MemoryCacheBytes = cache,
            // Without a limit Docker reports the host's memory; the collector drops a limit that is not below it.
            MemoryLimitBytes = memory?.Limit ?? 0,
            Network = stats.Networks is { Count: > 0 } networks
                ? new NetworkCounters(
                    networks.Values.Aggregate(0UL, (sum, n) => sum + n.RxBytes),
                    networks.Values.Aggregate(0UL, (sum, n) => sum + n.TxBytes))
                : null,
            BlockReadBytes = SumOp(blkio, "read"),
            BlockWriteBytes = SumOp(blkio, "write"),
            Pids = (uint)Math.Min(stats.PidsStats?.Current ?? 0, uint.MaxValue),
        };
    }

    public static DockerDiskUsage? ToDiskUsage(SystemDataUsageInfoResponse usage)
    {
        // Daemons before API 1.52 answer with the older layout, which this client does not read.
        if (usage.ImageUsage is null && usage.ContainerUsage is null && usage.VolumeUsage is null)
        {
            return null;
        }

        var result = new DockerDiskUsage
        {
            ImagesBytes = Bytes(usage.ImageUsage?.TotalSize),
            ImagesReclaimableBytes = Bytes(usage.ImageUsage?.Reclaimable),
            ContainersBytes = Bytes(usage.ContainerUsage?.TotalSize),
            VolumesBytes = Bytes(usage.VolumeUsage?.TotalSize),
            BuildCacheBytes = Bytes(usage.BuildCacheUsage?.TotalSize),
        };

        foreach (var container in usage.ContainerUsage?.Items ?? [])
        {
            if (container.ID is { Length: > 0 } id && container.SizeRw is { } size)
            {
                result.Containers.Add(new Contracts.Agent.ContainerDiskUsage
                {
                    ContainerId = id,
                    Name = container.Names is { Count: > 0 } names ? names[0].TrimStart('/') : "",
                    WritableLayerBytes = Bytes(size),
                });
            }
        }

        foreach (var volume in usage.VolumeUsage?.Items ?? [])
        {
            // -1 means Docker could not measure the volume (e.g. a driver other than local).
            if (volume.Name is { Length: > 0 } name && volume.UsageData is { Size: >= 0 } data)
            {
                result.Volumes.Add(new Contracts.Agent.VolumeDiskUsage
                {
                    Name = name,
                    StackName = volume.Labels is not null && volume.Labels.TryGetValue(ContainerInfoMapper.ProjectLabel, out var stack) ? stack : "",
                    SizeBytes = Bytes(data.Size),
                });
            }
        }

        return result;
    }

    private static ulong? Stat(IDictionary<string, ulong> stats, string key) =>
        stats.TryGetValue(key, out var value) ? value : null;

    private static ulong SumOp(IEnumerable<BlkioStatEntry> entries, string op) =>
        entries.Where(e => string.Equals(e.Op, op, StringComparison.OrdinalIgnoreCase)).Aggregate(0UL, (sum, e) => sum + e.Value);

    private static ulong Bytes(long? value) => value is > 0 ? (ulong)value.Value : 0;
}
