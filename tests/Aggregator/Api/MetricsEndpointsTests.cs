using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class MetricsEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Samples_from_the_agent_show_up_as_current_usage_and_raw_series()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var now = DateTimeOffset.UtcNow;

        await agent.SendAsync(Sample(now.AddSeconds(-10), cpu: 0.5, hasRates: false));
        await agent.SendAsync(Sample(now, cpu: 1.5, hasRates: true));

        var current = await WaitForCurrentAsync(client, agent.EnvironmentId, now);
        Assert.Equal(2U, current.GetProperty("host").GetProperty("cpuCount").GetUInt32());
        var container = Assert.Single(current.GetProperty("containers").EnumerateArray());
        Assert.Equal("shop-web-1", container.GetProperty("name").GetString());
        Assert.Equal(1.5, container.GetProperty("cpuCores").GetDouble());

        var metrics = await client.GetFromJsonAsync<JsonElement>(
            $"/api/environments/{agent.EnvironmentId}/metrics?range=15m", AggregatorApp.Json, Ct);
        Assert.Equal("Raw", metrics.GetProperty("window").GetProperty("resolution").GetString());
        Assert.Equal(2, metrics.GetProperty("host").GetProperty("at").GetArrayLength());
        var stack = Assert.Single(metrics.GetProperty("stacks").EnumerateArray());
        Assert.Equal("shop", stack.GetProperty("name").GetString());
        Assert.Equal([0.5, 1.5], stack.GetProperty("cpu").EnumerateArray().Select(v => v.GetDouble()));
    }

    [Fact]
    public async Task An_unknown_range_is_a_bad_request()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest, app.CreateClient().GetAsync($"/api/environments/{agent.EnvironmentId}/metrics?range=2y", Ct));
    }

    [Fact]
    public async Task Longer_ranges_read_stored_minutes()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var metrics = await app.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/api/environments/{agent.EnvironmentId}/metrics?range=24h", AggregatorApp.Json, Ct);

        Assert.Equal("Minute", metrics.GetProperty("window").GetProperty("resolution").GetString());
        Assert.Equal(300, metrics.GetProperty("window").GetProperty("stepSeconds").GetInt64());
        Assert.Equal(0, metrics.GetProperty("host").GetProperty("at").GetArrayLength());
    }

    [Fact]
    public async Task Settings_can_be_changed_by_an_administrator()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/api/settings/metrics",
            new
            {
                minuteRetentionDays = 3, hourRetentionDays = 30, alertsEnabled = true, hostCpuPercent = 0, hostMemoryPercent = 95,
                diskPercent = 85, containerMemoryPercent = 90, alertMinutes = 10,
            },
            AggregatorApp.Json,
            Ct);
        response.EnsureSuccessStatusCode();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/metrics", AggregatorApp.Json, Ct);
        Assert.Equal("3", settings.GetProperty("minuteRetentionDays").GetProperty("value").GetString());
        Assert.Equal("0", settings.GetProperty("hostCpuPercent").GetProperty("value").GetString());
        Assert.Equal("Database", settings.GetProperty("diskPercent").GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_retention_out_of_range_is_rejected()
    {
        await using var app = new AggregatorApp();

        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            app.CreateClient().PutAsJsonAsync(
                "/api/settings/metrics",
                new
                {
                    minuteRetentionDays = 365, hourRetentionDays = 30, alertsEnabled = true, hostCpuPercent = 90, hostMemoryPercent = 90,
                    diskPercent = 90, containerMemoryPercent = 90, alertMinutes = 5,
                },
                AggregatorApp.Json,
                Ct));
    }

    private static async Task<JsonElement> WaitForCurrentAsync(HttpClient client, Guid environmentId, DateTimeOffset at)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current = await client.GetFromJsonAsync<JsonElement>($"/api/environments/{environmentId}/metrics/current", AggregatorApp.Json, Ct);
            if (current.GetProperty("at").ValueKind == JsonValueKind.String
                && current.GetProperty("at").GetDateTimeOffset() >= at.AddMilliseconds(-1))
            {
                return current;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The aggregator did not take the sample.");
    }

    private static AgentMessage Sample(DateTimeOffset at, double cpu, bool hasRates) => new()
    {
        Metrics = new MetricsSample
        {
            TimestampUnixMs = at.ToUnixTimeMilliseconds(),
            IntervalMs = 10_000,
            Host = new HostMetrics
            {
                CpuCount = 2, HasRates = hasRates, CpuUserRatio = 0.25, MemoryTotalBytes = 8_000_000_000, MemoryAvailableBytes = 6_000_000_000,
                Filesystems = { new FilesystemMetrics { MountPoint = "/", Device = "/dev/sda1", Type = "ext4", TotalBytes = 100, UsedBytes = 50, AvailableBytes = 50 } },
            },
            Containers =
            {
                new ContainerMetrics
                {
                    ContainerId = "abc123", Name = "shop-web-1", StackName = "shop", ServiceName = "web", HasRates = hasRates,
                    CpuCores = cpu, MemoryWorkingSetBytes = 100_000_000, Pids = 3,
                },
            },
        },
    };
}
