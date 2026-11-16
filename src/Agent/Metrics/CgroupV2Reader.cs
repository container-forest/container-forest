namespace ContainerForest.Agent.Metrics;

/// <summary>
/// Reads a container's counters straight from the host's cgroup v2 files: a few small reads per container instead of
/// a Docker stats call, which keeps the daemon out of the way every few seconds.
/// </summary>
public sealed class CgroupV2Reader(HostFiles files)
{
    private const string CgroupRoot = "sys/fs/cgroup";

    public bool IsAvailable => files.Exists($"{CgroupRoot}/cgroup.controllers");

    /// <summary>
    /// The container's cgroup directory relative to the cgroup root: the systemd driver's
    /// system.slice/docker-&lt;id&gt;.scope, the cgroupfs driver's docker/&lt;id&gt;, or what /proc/&lt;pid&gt;/cgroup says
    /// (a cgroup_parent set in compose). Null when none exists.
    /// </summary>
    public string? FindDirectory(string containerId, long? pid)
    {
        string[] candidates = [$"system.slice/docker-{containerId}.scope", $"docker/{containerId}"];
        foreach (var candidate in candidates)
        {
            if (files.Exists($"{CgroupRoot}/{candidate}/cgroup.procs"))
            {
                return candidate;
            }
        }

        if (pid is { } p && files.ReadText($"proc/{p}/cgroup") is { } content
            && CgroupParsers.ParseProcCgroup(content) is { Length: > 0 } path
            && files.Exists($"{CgroupRoot}/{path}/cgroup.procs"))
        {
            return path;
        }

        return null;
    }

    /// <summary>Null when the directory is gone (the container stopped since it was listed).</summary>
    public ContainerCounters? Read(string directory)
    {
        var dir = $"{CgroupRoot}/{directory}";
        if (files.ReadText($"{dir}/cpu.stat") is not { } cpuStat || files.ReadText($"{dir}/memory.current") is not { } memoryCurrent)
        {
            return null;
        }

        var cpu = CgroupParsers.ParseFlatKeyed(cpuStat);
        var memory = CgroupParsers.ParseFlatKeyed(files.ReadText($"{dir}/memory.stat") ?? "");
        var usage = CgroupParsers.ParseSingleOrMax(memoryCurrent);
        var inactive = memory.GetValueOrDefault("inactive_file");
        var (read, write) = CgroupParsers.ParseIoStat(files.ReadText($"{dir}/io.stat") ?? "");

        return new ContainerCounters
        {
            CpuUsageNanoseconds = cpu.GetValueOrDefault("usage_usec") * 1000,
            CpuPeriods = cpu.GetValueOrDefault("nr_periods"),
            CpuThrottledPeriods = cpu.GetValueOrDefault("nr_throttled"),
            CpuLimitCores = CgroupParsers.ParseCpuMax(files.ReadText($"{dir}/cpu.max") ?? ""),
            MemoryWorkingSetBytes = usage > inactive ? usage - inactive : 0,
            MemoryCacheBytes = memory.GetValueOrDefault("file"),
            MemoryLimitBytes = CgroupParsers.ParseSingleOrMax(files.ReadText($"{dir}/memory.max") ?? ""),
            BlockReadBytes = read,
            BlockWriteBytes = write,
            Pids = (uint)Math.Min(CgroupParsers.ParseSingleOrMax(files.ReadText($"{dir}/pids.current") ?? ""), uint.MaxValue),
        };
    }

    /// <summary>Whether the process still belongs to the container: pids are reused after a restart.</summary>
    public bool BelongsTo(long pid, string directory) =>
        files.ReadText($"proc/{pid}/cgroup") is { } content
        && (CgroupParsers.ParseProcCgroup(content) is not { } path || path == directory);

    /// <summary>The traffic of the container's network namespace, through its main process; lo is not counted.</summary>
    public NetworkCounters? ReadNetwork(long pid)
    {
        if (files.ReadText($"proc/{pid}/net/dev") is not { } content)
        {
            return null;
        }

        var interfaces = ProcParsers.ParseNetDev(content).Where(i => i.Key != "lo").Select(i => i.Value).ToList();
        return new NetworkCounters(
            interfaces.Aggregate(0UL, (sum, i) => sum + i.RxBytes),
            interfaces.Aggregate(0UL, (sum, i) => sum + i.TxBytes));
    }
}
