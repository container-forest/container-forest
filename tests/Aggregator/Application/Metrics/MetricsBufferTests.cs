using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Tests.Metrics;

public class MetricsBufferTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly EnvironmentId Env = EnvironmentId.New();

    private readonly MovableTime time = new(Start);
    private readonly MetricsBuffer buffer;

    public MetricsBufferTests()
    {
        buffer = new MetricsBuffer(time);
    }

    [Fact]
    public void A_minute_is_finished_when_a_sample_of_the_next_one_arrives()
    {
        buffer.Add(Snapshot(Start, cpu: 1, memory: 100, hasRates: true));
        buffer.Add(Snapshot(Start.AddSeconds(30), cpu: 3, memory: 300, hasRates: true));
        Assert.Empty(buffer.TakeFinishedMinutes());

        buffer.Add(Snapshot(Start.AddSeconds(60), cpu: 9, memory: 900, hasRates: true));

        var batch = Assert.Single(buffer.TakeFinishedMinutes());
        var container = Assert.Single(batch.Containers);
        Assert.Equal(Start.ToUnixTimeSeconds(), container.Start);
        Assert.Equal(2, container.CpuAvg);
        Assert.Equal(3, container.CpuMax);
        Assert.Equal(200, container.MemoryAvg);
        Assert.Equal(300, container.MemoryMax);
    }

    [Fact]
    public void Samples_without_rates_count_for_memory_but_not_for_cpu()
    {
        buffer.Add(Snapshot(Start, cpu: 0, memory: 100, hasRates: false));
        buffer.Add(Snapshot(Start.AddSeconds(10), cpu: 2, memory: 300, hasRates: true));
        buffer.Add(Snapshot(Start.AddMinutes(1), cpu: 0, memory: 0, hasRates: true));

        var container = Assert.Single(Assert.Single(buffer.TakeFinishedMinutes()).Containers);
        Assert.Equal(2, container.CpuAvg);
        Assert.Equal(200, container.MemoryAvg);
    }

    [Fact]
    public void The_minute_of_a_silent_agent_is_finished_by_the_clock()
    {
        buffer.Add(Snapshot(Start.AddSeconds(5), cpu: 1, memory: 1, hasRates: true));

        time.Now += TimeSpan.FromSeconds(90);
        Assert.Empty(buffer.TakeFinishedMinutes());
        time.Now += TimeSpan.FromSeconds(60);

        Assert.Single(buffer.TakeFinishedMinutes());
    }

    [Fact]
    public void Raw_samples_are_kept_for_an_hour()
    {
        buffer.Add(Snapshot(Start, 1, 1, true));
        buffer.Add(Snapshot(Start.AddMinutes(30), 1, 1, true));
        buffer.Add(Snapshot(Start.AddMinutes(61), 1, 1, true));

        Assert.Equal([Start.AddMinutes(30), Start.AddMinutes(61)], buffer.GetSamples(Env, Start).Select(s => s.At));
        Assert.Equal(Start.AddMinutes(61), buffer.GetState(Env).Latest?.At);
    }

    [Fact]
    public void The_latest_disk_usage_is_kept_between_samples()
    {
        var usage = new DiskUsageSample(Start, 1, 0, 2, 3, 4, [], []);
        buffer.Add(Snapshot(Start, 1, 1, true) with { DiskUsage = usage });
        buffer.Add(Snapshot(Start.AddSeconds(10), 1, 1, true));

        Assert.Equal(usage, buffer.GetState(Env).DiskUsage);
    }

    [Fact]
    public void Failed_writes_are_returned_for_the_next_run()
    {
        buffer.Add(Snapshot(Start, 1, 1, true));
        buffer.Add(Snapshot(Start.AddMinutes(1), 1, 1, true));
        var batches = buffer.TakeFinishedMinutes();

        buffer.Return(batches);

        Assert.Equal(batches, buffer.TakeFinishedMinutes());
    }

    private static MetricsSnapshot Snapshot(DateTimeOffset at, double cpu, ulong memory, bool hasRates) => new(
        Env,
        at,
        new HostSample(hasRates, 2, 0.1, 0.1, 0, 0, 0.5, 0.5, 0.5, 8000, 6000, 0, 0, 0, 0,
            [new FilesystemSample("/", "/dev/sda1", "ext4", 100, 40, 60)], [new InterfaceSample("eth0", 10, 20)], 100),
        [new ContainerSample("abc", "shop-web-1", "shop", "web", hasRates, cpu, 0, 0, memory, 0, 0, 0, 0, false, 0, 0, 1)]);

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
