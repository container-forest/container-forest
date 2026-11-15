using ContainerForest.Agent.Metrics;

namespace ContainerForest.Agent.Tests.Metrics;

public class ParserTests
{
    [Theory]
    [InlineData("max 100000\n", 0)]
    [InlineData("150000 100000\n", 1.5)]
    [InlineData("50000 100000", 0.5)]
    [InlineData("", 0)]
    public void Cpu_max_is_read_as_cores(string content, double expected) =>
        Assert.Equal(expected, CgroupParsers.ParseCpuMax(content), 6);

    [Theory]
    [InlineData("max\n", 0UL)]
    [InlineData("536870912\n", 536870912UL)]
    public void Memory_max_of_max_is_no_limit(string content, ulong expected) =>
        Assert.Equal(expected, CgroupParsers.ParseSingleOrMax(content));

    [Fact]
    public void Io_stat_sums_bytes_of_all_devices()
    {
        var (read, write) = CgroupParsers.ParseIoStat(
            "8:0 rbytes=1000 wbytes=2000 rios=3 wios=4 dbytes=0 dios=0\n259:0 rbytes=10 wbytes=20 rios=1 wios=1\n");

        Assert.Equal(1010UL, read);
        Assert.Equal(2020UL, write);
    }

    [Theory]
    [InlineData("0::/system.slice/docker-abc.scope\n", "system.slice/docker-abc.scope")]
    [InlineData("0::/../../system.slice/docker-abc.scope\n", null)]
    [InlineData("12:memory:/docker/abc\n", null)]
    public void Proc_cgroup_gives_a_path_inside_the_namespace_only(string content, string? expected) =>
        Assert.Equal(expected, CgroupParsers.ParseProcCgroup(content));

    [Fact]
    public void Cpu_line_folds_nice_into_user_and_interrupts_into_system()
    {
        var cpu = ProcParsers.ParseCpuTimes("cpu  100 5 50 1000 20 3 2 7 0 0\ncpu0 100 5 50 1000 20 3 2 7 0 0\n");

        Assert.Equal(new CpuTimes(User: 105, System: 55, Idle: 1000, IoWait: 20, Steal: 7), cpu);
    }

    [Fact]
    public void Cpus_are_counted_by_their_lines()
    {
        Assert.Equal(2U, ProcParsers.CountCpus("cpu  1 2 3 4\ncpu0 1 2 3 4\ncpu1 1 2 3 4\nintr 5\n"));
    }

    [Fact]
    public void Meminfo_is_converted_to_bytes()
    {
        var memory = ProcParsers.ParseMemInfo("MemTotal:       16303548 kB\nMemAvailable:    8000000 kB\nHugePages_Total:       0\n");

        Assert.Equal(16303548UL * 1024, memory["MemTotal"]);
        Assert.Equal(8000000UL * 1024, memory["MemAvailable"]);
        Assert.Equal(0UL, memory["HugePages_Total"]);
    }

    [Fact]
    public void Net_dev_reads_rx_and_tx_bytes()
    {
        const string content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:    5000      50    0    0    0     0          0         0     5000      50    0    0    0     0       0          0
              eth0: 1234567    1000    0    0    0     0          0         0   765432     900    0    0    0     0       0          0
            """;

        var interfaces = ProcParsers.ParseNetDev(content);

        Assert.Equal(new NetworkCounters(1234567, 765432), interfaces["eth0"]);
        Assert.Equal(new NetworkCounters(5000, 5000), interfaces["lo"]);
    }

    [Fact]
    public void Diskstats_sectors_are_512_bytes()
    {
        var disks = ProcParsers.ParseDiskStats("   8       0 sda 100 0 2000 50 200 0 4000 80 0 120 130\n");

        Assert.Equal((2000UL * 512, 4000UL * 512), disks["sda"]);
    }

    [Fact]
    public void Mount_points_with_spaces_are_unescaped()
    {
        var mount = Assert.Single(ProcParsers.ParseMounts("/dev/sdb1 /mnt/my\\040disk ext4 rw,relatime 0 0\n"));

        Assert.Equal(("/dev/sdb1", "/mnt/my disk", "ext4"), mount);
    }

    [Fact]
    public void Load_and_uptime_are_read()
    {
        Assert.Equal((0.2, 0.18, 0.12), ProcParsers.ParseLoadAvg("0.20 0.18 0.12 1/80 11206\n"));
        Assert.Equal(350735L, ProcParsers.ParseUptime("350735.47 234388.90\n"));
    }
}
