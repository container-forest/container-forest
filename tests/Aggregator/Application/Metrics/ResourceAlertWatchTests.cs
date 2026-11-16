using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Metrics;

// Without services the watch keeps the default settings, 90 % thresholds held for 5 minutes, and only logs that it
// could not notify.
public sealed class ResourceAlertWatchTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly EnvironmentId Env = EnvironmentId.New();

    private readonly ResourceAlertWatch watch = new(
        new NoServices(),
        TimeProvider.System,
        NullLogger<ResourceAlertWatch>.Instance);

    [Fact]
    public void High_goes_out_once_the_threshold_held_for_the_alert_time()
    {
        Assert.Empty(watch.Observe(Snapshot(Start, diskUsedPercent: 95)));
        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(4), diskUsedPercent: 95)));

        var alert = Assert.Single(watch.Observe(Snapshot(Start.AddMinutes(5), diskUsedPercent: 95)));

        Assert.Equal((NotificationEventKinds.ResourceUsageHigh, ResourceAlertKind.Disk, "/"), (alert.Kind, alert.Alert, alert.Subject));
        Assert.Equal(90, alert.ThresholdPercent);
        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(10), diskUsedPercent: 95)));
    }

    [Fact]
    public void A_dip_below_the_threshold_starts_the_wait_over()
    {
        watch.Observe(Snapshot(Start, diskUsedPercent: 95));
        watch.Observe(Snapshot(Start.AddMinutes(3), diskUsedPercent: 50));

        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(6), diskUsedPercent: 95)));
        Assert.Single(watch.Observe(Snapshot(Start.AddMinutes(11), diskUsedPercent: 95)));
    }

    [Fact]
    public void Normal_closes_an_open_alert_after_the_same_time()
    {
        watch.Observe(Snapshot(Start, diskUsedPercent: 95));
        watch.Observe(Snapshot(Start.AddMinutes(5), diskUsedPercent: 95));

        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(6), diskUsedPercent: 50)));
        var alert = Assert.Single(watch.Observe(Snapshot(Start.AddMinutes(11), diskUsedPercent: 50)));

        Assert.Equal(NotificationEventKinds.ResourceUsageNormal, alert.Kind);
    }

    [Fact]
    public void Container_memory_counts_only_against_a_limit()
    {
        watch.Observe(Snapshot(Start, containerMemory: 950, containerLimit: 0));
        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(5), containerMemory: 950, containerLimit: 0)));

        watch.Observe(Snapshot(Start.AddMinutes(6), containerMemory: 950, containerLimit: 1000));
        var alert = Assert.Single(watch.Observe(Snapshot(Start.AddMinutes(11), containerMemory: 950, containerLimit: 1000)));

        Assert.Equal((ResourceAlertKind.ContainerMemory, "shop-web-1", "shop"), (alert.Alert, alert.Subject, alert.StackName));
    }

    [Fact]
    public void Host_cpu_is_judged_only_on_samples_with_rates()
    {
        watch.Observe(Snapshot(Start, cpuBusy: 0.99, hasRates: false));
        watch.Observe(Snapshot(Start.AddMinutes(1), cpuBusy: 0.99));

        Assert.Empty(watch.Observe(Snapshot(Start.AddMinutes(5), cpuBusy: 0.99)));
        Assert.Equal(ResourceAlertKind.HostCpu, Assert.Single(watch.Observe(Snapshot(Start.AddMinutes(6), cpuBusy: 0.99))).Alert);
    }

    private static MetricsSnapshot Snapshot(
        DateTimeOffset at,
        double diskUsedPercent = 10,
        ulong containerMemory = 0,
        ulong containerLimit = 0,
        double cpuBusy = 0.1,
        bool hasRates = true) => new(
        Env,
        at,
        new HostSample(hasRates, 2, cpuBusy, 0, 0, 0, 0, 0, 0, 1000, 900, 0, 0, 0, 0,
            [new FilesystemSample("/", "/dev/sda1", "ext4", 100, (ulong)(100 - diskUsedPercent), (ulong)diskUsedPercent)], [], 100),
        [new ContainerSample("abc", "shop-web-1", "shop", "web", true, 0, 0, 0, containerMemory, 0, containerLimit, 0, 0, false, 0, 0, 1)]);

    public void Dispose() => watch.Dispose();

    private sealed class NoServices : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("No services in this test.");
    }
}
