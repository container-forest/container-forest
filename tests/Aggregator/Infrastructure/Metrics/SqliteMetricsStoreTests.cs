using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Metrics;

public sealed class SqliteMetricsStoreTests : IDisposable
{
    private static readonly EnvironmentId Env = EnvironmentId.New();
    private readonly string path = Path.Combine(Path.GetTempPath(), $"cf-metrics-{Guid.NewGuid():N}.db");
    private readonly SqliteMetricsStore store;

    public SqliteMetricsStoreTests()
    {
        store = new SqliteMetricsStore($"Data Source={path}");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Minutes_are_grouped_into_buckets_of_the_step()
    {
        await store.AppendAsync(
            [Batch(0, cpu: 1, memory: 100), Batch(60, cpu: 3, memory: 300), Batch(120, cpu: 5, memory: 50)], Ct);

        var points = await store.QueryContainersAsync(Env, MetricsResolution.Minute, ContainerFilter.All, 0, 180, 120, Ct);

        Assert.Equal([0L, 120L], points.Select(p => p.Start));
        Assert.Equal(2, points[0].CpuAvg);
        Assert.Equal(3, points[0].CpuMax);
        Assert.Equal(300, points[0].MemoryMax);
        Assert.Equal("shop", points[0].Stack);
    }

    [Fact]
    public async Task Filters_select_a_container_or_a_stack()
    {
        await store.AppendAsync([Batch(0, 1, 1), Batch(0, 1, 1, container: "db-1", stack: "db")], Ct);

        Assert.Equal(["db-1"], (await store.QueryContainersAsync(Env, MetricsResolution.Minute, new ContainerFilter(Stack: "db"), 0, 60, 60, Ct)).Select(p => p.Container));
        Assert.Equal(["web-1"], (await store.QueryContainersAsync(Env, MetricsResolution.Minute, new ContainerFilter(Container: "web-1"), 0, 60, 60, Ct)).Select(p => p.Container));
    }

    [Fact]
    public async Task Writing_the_same_minute_again_replaces_it()
    {
        await store.AppendAsync([Batch(0, cpu: 1, memory: 1)], Ct);
        await store.AppendAsync([Batch(0, cpu: 7, memory: 1)], Ct);

        var point = Assert.Single(await store.QueryContainersAsync(Env, MetricsResolution.Minute, ContainerFilter.All, 0, 60, 60, Ct));
        Assert.Equal(7, point.CpuAvg);
    }

    [Fact]
    public async Task Hours_are_rolled_up_from_minutes()
    {
        await store.AppendAsync([Batch(3600, 2, 10), Batch(3660, 4, 30), Batch(7200, 100, 100)], Ct);

        await store.RollUpHoursAsync(3600, 7200, Ct);

        var container = Assert.Single(await store.QueryContainersAsync(Env, MetricsResolution.Hour, ContainerFilter.All, 0, 10_800, 3600, Ct));
        Assert.Equal((3600L, 3.0, 4.0, 30.0), (container.Start, container.CpuAvg, container.CpuMax, container.MemoryMax));
        var host = Assert.Single(await store.QueryHostAsync(Env, MetricsResolution.Hour, 0, 10_800, 3600, Ct));
        Assert.Equal(3600L, host.Start);
        var filesystem = Assert.Single(await store.QueryFilesystemsAsync(Env, MetricsResolution.Hour, 0, 10_800, 3600, Ct));
        Assert.Equal("/", filesystem.MountPoint);
    }

    [Fact]
    public async Task Expired_points_and_points_of_deleted_environments_are_removed()
    {
        var gone = EnvironmentId.New();
        await store.AppendAsync([Batch(0, 1, 1), Batch(600, 1, 1), Batch(600, 1, 1, environment: gone)], Ct);

        var removed = await store.RemoveExpiredAsync(minutesBefore: 300, hoursBefore: 0, [Env], Ct);

        Assert.True(removed >= 2);
        Assert.Equal([600L], (await store.QueryContainersAsync(Env, MetricsResolution.Minute, ContainerFilter.All, 0, 1000, 60, Ct)).Select(p => p.Start));
        Assert.Empty(await store.QueryContainersAsync(gone, MetricsResolution.Minute, ContainerFilter.All, 0, 1000, 60, Ct));
    }

    private static MinuteBatch Batch(long start, double cpu, double memory, string container = "web-1", string stack = "shop", EnvironmentId? environment = null)
    {
        var env = environment ?? Env;
        return new MinuteBatch(
            new HostPoint(env, start, 2, 0.1, 0.1, 0, 0, 0.3, 0.5, 1000, 1200, 8000, 0, 0, 10, 20, 30, 40),
            [new ContainerPoint(env, container, start, stack, "web", cpu, cpu, 0, 0, memory, memory, 0, 0, 1, 2, 3, 4, 5)],
            [new FilesystemPoint(env, "/", start, 50, 100)]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
