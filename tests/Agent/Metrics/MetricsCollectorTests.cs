using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Metrics;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Metrics;

public sealed class MetricsCollectorTests : IDisposable
{
    private const string Id = "0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HostTree tree = new();
    private readonly FakeMetricsGateway docker = new();
    private readonly SteppedTimeProvider time = new(DateTimeOffset.Parse("2026-11-15T12:00:00Z"));

    public MetricsCollectorTests()
    {
        docker.Running.Add(new RunningContainer(Id, "shop-web-1", "shop", "web", HostNetwork: false));
    }

    [Fact]
    public async Task Metrics_can_be_turned_off()
    {
        var collector = CreateCollector(TimeSpan.Zero);

        Assert.Equal(MetricsSource.Disabled, await collector.DetectSourceAsync(Ct));
    }

    [Fact]
    public async Task Docker_stats_are_used_without_the_cgroup_v2_hierarchy()
    {
        docker.Stats[Id] = new ContainerCounters { CpuUsageNanoseconds = 1, MemoryWorkingSetBytes = 42 };
        var collector = CreateCollector();

        Assert.Equal(MetricsSource.DockerStats, await collector.DetectSourceAsync(Ct));
        var container = Assert.Single((await collector.CollectAsync(Ct)).Containers);
        Assert.Equal(42UL, container.MemoryWorkingSetBytes);
        Assert.Equal("shop", container.StackName);
    }

    [Fact]
    public async Task Docker_stats_are_used_on_cgroup_v1_hosts_even_with_v2_files_present()
    {
        tree.File("sys/fs/cgroup/cgroup.controllers", "");
        docker.Runtime = new DockerRuntime("1", "cgroupfs");

        Assert.Equal(MetricsSource.DockerStats, await CreateCollector().DetectSourceAsync(Ct));
    }

    [Fact]
    public async Task Cgroup_counters_turn_into_rates_on_the_second_sample()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        tree.File("sys/fs/cgroup/cgroup.controllers", "")
            .Cgroup($"system.slice/docker-{Id}.scope", usageUsec: 1_000_000)
            .File("proc/42/cgroup", $"0::/system.slice/docker-{Id}.scope\n")
            .NetDev(42, rx: 0, tx: 0);
        docker.Pids[Id] = 42;
        var collector = CreateCollector();
        Assert.Equal(MetricsSource.CgroupV2, await collector.DetectSourceAsync(Ct));

        var first = Assert.Single((await collector.CollectAsync(Ct)).Containers);
        tree.Cgroup($"system.slice/docker-{Id}.scope", usageUsec: 6_000_000).NetDev(42, rx: 10_000, tx: 0);
        time.Advance(TimeSpan.FromSeconds(10));
        var sample = await collector.CollectAsync(Ct);
        var second = Assert.Single(sample.Containers);

        Assert.False(first.HasRates);
        Assert.True(second.HasRates);
        Assert.Equal(0.5, second.CpuCores, 6);
        Assert.Equal(1000, second.NetworkRxBytesPerSecond, 6);
        Assert.Equal(10_000, sample.IntervalMs);
    }

    [Fact]
    public async Task A_container_without_a_visible_cgroup_falls_back_to_docker_stats()
    {
        tree.File("sys/fs/cgroup/cgroup.controllers", "");
        docker.Stats[Id] = new ContainerCounters { MemoryWorkingSetBytes = 7 };
        var collector = CreateCollector();
        await collector.DetectSourceAsync(Ct);

        var container = Assert.Single((await collector.CollectAsync(Ct)).Containers);

        Assert.Equal(7UL, container.MemoryWorkingSetBytes);
    }

    [Fact]
    public async Task Docker_being_unreachable_fails_the_sample()
    {
        var collector = CreateCollector();
        await collector.DetectSourceAsync(Ct);
        docker.Failure = new DockerUnavailableException(new IOException("gone"));

        await Assert.ThrowsAsync<DockerUnavailableException>(() => collector.CollectAsync(Ct));
    }

    private MetricsCollector CreateCollector(TimeSpan? interval = null) =>
        new(
            docker,
            Options.Create(new AgentOptions { HostRoot = tree.Root, MetricsInterval = interval ?? TimeSpan.FromSeconds(10) }),
            time,
            NullLogger<MetricsCollector>.Instance);

    public void Dispose() => tree.Dispose();
}
