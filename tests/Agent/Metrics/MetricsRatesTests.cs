using ContainerForest.Agent.Metrics;

namespace ContainerForest.Agent.Tests.Metrics;

public class MetricsRatesTests
{
    private static readonly RunningContainer Web = new("abc", "shop-web-1", "shop", "web", HostNetwork: false);

    private static readonly ContainerCounters Before = new()
    {
        CpuUsageNanoseconds = 10_000_000_000,
        CpuPeriods = 100,
        CpuThrottledPeriods = 10,
        MemoryWorkingSetBytes = 100,
        Network = new NetworkCounters(1000, 2000),
        BlockReadBytes = 0,
        BlockWriteBytes = 0,
    };

    [Fact]
    public void Rates_are_averages_over_the_elapsed_time()
    {
        var after = Before with
        {
            CpuUsageNanoseconds = 25_000_000_000,
            CpuPeriods = 200,
            CpuThrottledPeriods = 35,
            Network = new NetworkCounters(11_000, 7000),
            BlockWriteBytes = 50_000,
        };

        var metrics = MetricsRates.ToContainerMetrics(Web, after, Before, TimeSpan.FromSeconds(10), hostMemoryBytes: 0);

        Assert.True(metrics.HasRates);
        Assert.Equal(1.5, metrics.CpuCores, 6);
        Assert.Equal(0.25, metrics.CpuThrottledRatio, 6);
        Assert.Equal(1000, metrics.NetworkRxBytesPerSecond, 6);
        Assert.Equal(500, metrics.NetworkTxBytesPerSecond, 6);
        Assert.Equal(5000, metrics.BlockWriteBytesPerSecond, 6);
    }

    [Fact]
    public void The_first_reading_has_gauges_but_no_rates()
    {
        var metrics = MetricsRates.ToContainerMetrics(Web, Before, previous: null, TimeSpan.Zero, hostMemoryBytes: 0);

        Assert.False(metrics.HasRates);
        Assert.Equal(100UL, metrics.MemoryWorkingSetBytes);
        Assert.Equal(0, metrics.CpuCores);
    }

    [Fact]
    public void A_counter_going_down_means_a_restart_and_gives_no_rates()
    {
        var restarted = Before with { CpuUsageNanoseconds = 1_000_000 };

        var metrics = MetricsRates.ToContainerMetrics(Web, restarted, Before, TimeSpan.FromSeconds(10), hostMemoryBytes: 0);

        Assert.False(metrics.HasRates);
    }

    [Fact]
    public void A_memory_limit_not_below_the_host_memory_is_no_limit()
    {
        var counters = Before with { MemoryLimitBytes = 8_000_000_000 };

        Assert.Equal(0UL, MetricsRates.ToContainerMetrics(Web, counters, null, TimeSpan.Zero, hostMemoryBytes: 8_000_000_000).MemoryLimitBytes);
        Assert.Equal(8_000_000_000UL, MetricsRates.ToContainerMetrics(Web, counters, null, TimeSpan.Zero, hostMemoryBytes: 16_000_000_000).MemoryLimitBytes);
    }

    [Fact]
    public void Host_network_containers_report_no_traffic_of_their_own()
    {
        var host = Web with { HostNetwork = true };
        var after = Before with { CpuUsageNanoseconds = 11_000_000_000, Network = new NetworkCounters(99_000, 99_000) };

        var metrics = MetricsRates.ToContainerMetrics(host, after, Before, TimeSpan.FromSeconds(10), hostMemoryBytes: 0);

        Assert.True(metrics.HostNetwork);
        Assert.Equal(0, metrics.NetworkRxBytesPerSecond);
    }

    [Fact]
    public void Host_cpu_ratios_are_shares_of_all_ticks()
    {
        var before = new HostCounters { Cpu = new CpuTimes(100, 100, 700, 50, 50), UptimeSeconds = 100 };
        var after = before with
        {
            Cpu = new CpuTimes(User: 300, System: 200, Idle: 1200, IoWait: 100, Steal: 200),
            UptimeSeconds = 110,
            Interfaces = new Dictionary<string, NetworkCounters> { ["eth0"] = new(10_000, 0) },
        };

        var metrics = MetricsRates.ToHostMetrics(after, before, TimeSpan.FromSeconds(10));

        Assert.True(metrics.HasRates);
        Assert.Equal(0.2, metrics.CpuUserRatio, 6);
        Assert.Equal(0.1, metrics.CpuSystemRatio, 6);
        Assert.Equal(0.05, metrics.CpuIowaitRatio, 6);
        Assert.Equal(0.15, metrics.CpuStealRatio, 6);
        // eth0 is new since the previous reading: listed, without a rate.
        var eth0 = Assert.Single(metrics.Interfaces);
        Assert.Equal(0, eth0.RxBytesPerSecond);
    }

    [Fact]
    public void A_host_reboot_between_readings_gives_no_rates()
    {
        var before = new HostCounters { Cpu = new CpuTimes(100, 100, 700, 50, 50), UptimeSeconds = 100_000 };
        var after = before with { Cpu = new CpuTimes(200, 200, 1400, 100, 100), UptimeSeconds = 30 };

        Assert.False(MetricsRates.ToHostMetrics(after, before, TimeSpan.FromSeconds(10)).HasRates);
    }
}
