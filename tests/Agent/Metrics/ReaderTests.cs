using ContainerForest.Agent.Metrics;

namespace ContainerForest.Agent.Tests.Metrics;

public sealed class ReaderTests : IDisposable
{
    private const string Id = "0123456789abcdef";

    private readonly HostTree tree = new();

    [Fact]
    public void Cgroup_v2_is_available_when_the_unified_hierarchy_is_mounted()
    {
        Assert.False(new CgroupV2Reader(new HostFiles(tree.Root)).IsAvailable);

        tree.File("sys/fs/cgroup/cgroup.controllers", "cpu io memory pids\n");

        Assert.True(new CgroupV2Reader(new HostFiles(tree.Root)).IsAvailable);
    }

    [Theory]
    [InlineData("system.slice/docker-0123456789abcdef.scope")]
    [InlineData("docker/0123456789abcdef")]
    public void The_directories_of_both_cgroup_drivers_are_found(string directory)
    {
        tree.Cgroup(directory, usageUsec: 1);

        Assert.Equal(directory, new CgroupV2Reader(new HostFiles(tree.Root)).FindDirectory(Id, pid: null));
    }

    [Fact]
    public void A_cgroup_parent_is_found_through_the_main_process()
    {
        tree.Cgroup("custom.slice/app", usageUsec: 1).File("proc/42/cgroup", "0::/custom.slice/app\n");

        var reader = new CgroupV2Reader(new HostFiles(tree.Root));

        Assert.Equal("custom.slice/app", reader.FindDirectory(Id, pid: 42));
        Assert.True(reader.BelongsTo(42, "custom.slice/app"));
        Assert.False(reader.BelongsTo(42, "docker/other"));
    }

    [Fact]
    public void Counters_are_read_from_the_interface_files()
    {
        tree.Cgroup("docker/" + Id, usageUsec: 2_500_000, memory: 1000, cpuMax: "200000 100000", memoryMax: "536870912");

        var counters = new CgroupV2Reader(new HostFiles(tree.Root)).Read("docker/" + Id);

        Assert.NotNull(counters);
        Assert.Equal(2_500_000_000UL, counters.CpuUsageNanoseconds);
        Assert.Equal(2.0, counters.CpuLimitCores);
        Assert.Equal(900UL, counters.MemoryWorkingSetBytes);
        Assert.Equal(400UL, counters.MemoryCacheBytes);
        Assert.Equal(536870912UL, counters.MemoryLimitBytes);
        Assert.Equal((10UL, 20UL), (counters.BlockReadBytes, counters.BlockWriteBytes));
        Assert.Equal(7U, counters.Pids);
    }

    [Fact]
    public void A_gone_cgroup_reads_as_null()
    {
        Assert.Null(new CgroupV2Reader(new HostFiles(tree.Root)).Read("docker/" + Id));
    }

    [Fact]
    public void Container_network_skips_loopback()
    {
        tree.NetDev(42, rx: 300, tx: 400);

        Assert.Equal(new NetworkCounters(300, 400), new CgroupV2Reader(new HostFiles(tree.Root)).ReadNetwork(42));
    }

    [Fact]
    public void Host_counters_count_physical_disks_and_real_interfaces_only()
    {
        tree.File("proc/stat", "cpu  1 0 1 10 0 0 0 0 0 0\ncpu0 1 0 1 10 0 0 0 0 0 0\ncpu1 0 0 0 0 0 0 0 0 0 0\n")
            .File("proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")
            .File("proc/loadavg", "1.00 0.50 0.25 1/1 1\n")
            .File("proc/uptime", "100.5 50\n")
            .File("proc/diskstats", "8 0 sda 1 0 10 0 1 0 20 0 0 0 0\n8 1 sda1 1 0 10 0 1 0 20 0 0 0 0\n7 0 loop0 1 0 99 0 0 0 0 0 0 0 0\n")
            .Directory_("sys/block/sda/device")
            .Directory_("sys/block/loop0/device")
            .File("proc/1/net/dev", "h1\nh2\n lo: 1 0 0 0 0 0 0 0 1 0 0 0 0 0 0 0\n eth0: 5 0 0 0 0 0 0 0 6 0 0 0 0 0 0 0\n docker0: 7 0 0 0 0 0 0 0 7 0 0 0 0 0 0 0\n veth1a2b: 8 0 0 0 0 0 0 0 8 0 0 0 0 0 0 0\n wt0: 9 0 0 0 0 0 0 0 9 0 0 0 0 0 0 0\n")
            .File("proc/1/mounts", "/dev/sda1 / ext4 rw 0 0\n/dev/sda1 /nix/store ext4 ro 0 0\ntmpfs /run tmpfs rw 0 0\noverlay /var/lib/docker/overlay2/x/merged overlay rw 0 0\n");

        var counters = new HostMetricsReader(new HostFiles(tree.Root)).Read();

        Assert.NotNull(counters);
        Assert.Equal(2U, counters.CpuCount);
        Assert.Equal(1000UL * 1024, counters.MemoryTotalBytes);
        Assert.Equal(100L, counters.UptimeSeconds);
        Assert.Equal((10UL * 512, 20UL * 512), (counters.DiskReadBytes, counters.DiskWriteBytes));
        Assert.Equal(["eth0", "wt0"], counters.Interfaces.Keys.Order());
        var root = Assert.Single(counters.Filesystems);
        Assert.Equal("/", root.MountPoint);
        Assert.True(root.TotalBytes > 0);
    }

    [Fact]
    public void Host_counters_are_null_without_proc()
    {
        Assert.Null(new HostMetricsReader(new HostFiles(tree.Root)).Read());
    }

    public void Dispose() => tree.Dispose();
}
