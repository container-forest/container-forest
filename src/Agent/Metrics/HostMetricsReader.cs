namespace ContainerForest.Agent.Metrics;

/// <summary>Reads the host's CPU, memory, disk and network counters from /proc and /sys.</summary>
public sealed class HostMetricsReader(HostFiles files)
{
    // File systems on real disks; tmpfs, overlay, squashfs, nsfs and the like are not where data runs out.
    private static readonly HashSet<string> DiskFilesystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "bcachefs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3",
    };

    // Docker's own mounts of the same devices: layers, volumes and container files would repeat the host's disks.
    private static readonly string[] SkippedMountPrefixes = ["/var/lib/docker/", "/var/lib/containerd/", "/run/", "/proc/", "/sys/"];

    /// <summary>Null when /proc/stat cannot be read (not Linux, or the host root is not mounted).</summary>
    public HostCounters? Read()
    {
        if (files.ReadText("proc/stat") is not { } stat || ProcParsers.ParseCpuTimes(stat) is not { } cpu)
        {
            return null;
        }

        var memory = ProcParsers.ParseMemInfo(files.ReadText("proc/meminfo") ?? "");
        var (load1, load5, load15) = ProcParsers.ParseLoadAvg(files.ReadText("proc/loadavg") ?? "");
        var (diskRead, diskWrite) = ReadDisks();

        return new HostCounters
        {
            CpuCount = ProcParsers.CountCpus(stat),
            Cpu = cpu,
            Load1 = load1,
            Load5 = load5,
            Load15 = load15,
            MemoryTotalBytes = memory.GetValueOrDefault("MemTotal"),
            MemoryAvailableBytes = memory.GetValueOrDefault("MemAvailable"),
            SwapTotalBytes = memory.GetValueOrDefault("SwapTotal"),
            SwapFreeBytes = memory.GetValueOrDefault("SwapFree"),
            UptimeSeconds = ProcParsers.ParseUptime(files.ReadText("proc/uptime") ?? ""),
            Filesystems = ReadFilesystems(),
            Interfaces = ReadInterfaces(),
            DiskReadBytes = diskRead,
            DiskWriteBytes = diskWrite,
        };
    }

    // Only devices with a "device" link in /sys/block are disks; partitions, loop, zram and device-mapper volumes would
    // count the same bytes again.
    private (ulong Read, ulong Write) ReadDisks()
    {
        ulong read = 0, write = 0;
        foreach (var (name, bytes) in ProcParsers.ParseDiskStats(files.ReadText("proc/diskstats") ?? ""))
        {
            if (!name.StartsWith("loop", StringComparison.Ordinal) && files.Exists($"sys/block/{name}/device"))
            {
                read += bytes.Read;
                write += bytes.Write;
            }
        }

        return (read, write);
    }

    private Dictionary<string, NetworkCounters> ReadInterfaces() =>
        ProcParsers.ParseNetDev(files.ReadText("proc/1/net/dev") ?? "")
            .Where(i => !IsVirtualInterface(i.Key))
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    public static bool IsVirtualInterface(string name) =>
        name == "lo" || name == "docker0"
        || name.StartsWith("veth", StringComparison.Ordinal)
        || name.StartsWith("br-", StringComparison.Ordinal);

    private List<FilesystemUsage> ReadFilesystems()
    {
        var result = new List<FilesystemUsage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mounts = ProcParsers.ParseMounts(files.ReadText("proc/1/mounts") ?? "")
            .Where(m => DiskFilesystems.Contains(m.Type) && !SkippedMountPrefixes.Any(p => m.MountPoint.StartsWith(p, StringComparison.Ordinal)))
            // The shortest mount point of a device is the one people know it by (/ rather than /nix/store).
            .OrderBy(m => m.MountPoint.Length);

        foreach (var (device, mountPoint, type) in mounts)
        {
            if (!seen.Add(device))
            {
                continue;
            }

            try
            {
                var drive = new DriveInfo(files.PathOf(mountPoint));
                var total = (ulong)drive.TotalSize;
                var free = (ulong)drive.TotalFreeSpace;
                result.Add(new FilesystemUsage(mountPoint, device, type, total, (ulong)drive.AvailableFreeSpace, total > free ? total - free : 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Mounted after the agent started without rslave, or gone since /proc/1/mounts was read.
            }
        }

        return result;
    }
}
