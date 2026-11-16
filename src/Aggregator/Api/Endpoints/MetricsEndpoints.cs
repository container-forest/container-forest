using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Resource usage graphs and their settings (see MetricsContracts).</summary>
internal static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Metrics")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/environments/{id:guid}/metrics/current", async (Guid id, MetricsQueryService service, CancellationToken ct) =>
                ToResponse(await service.GetCurrentAsync(EnvironmentId.From(id), ct)))
            .WithName("GetCurrentMetrics");

        api.MapGet("/environments/{id:guid}/metrics", async (Guid id, string? range, MetricsQueryService service, CancellationToken ct) =>
                ToResponse(await service.GetEnvironmentAsync(EnvironmentId.From(id), ParseRange(range), ct)))
            .WithName("GetEnvironmentMetrics")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/stacks/{id:guid}/metrics", async (Guid id, string? range, MetricsQueryService service, CancellationToken ct) =>
                ToResponse(await service.GetStackAsync(StackId.From(id), ParseRange(range), ct)))
            .WithName("GetStackMetrics")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet(
                "/environments/{id:guid}/unmanaged-stacks/{name}/metrics",
                async (Guid id, string name, string? range, MetricsQueryService service, CancellationToken ct) =>
                    ToResponse(await service.GetUnmanagedStackAsync(EnvironmentId.From(id), name, ParseRange(range), ct)))
            .WithName("GetUnmanagedStackMetrics")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet(
                "/environments/{id:guid}/containers/{containerId}/metrics",
                async (Guid id, string containerId, string? range, MetricsQueryService service, CancellationToken ct) =>
                    ToResponse(await service.GetContainerAsync(EnvironmentId.From(id), containerId, ParseRange(range), ct)))
            .WithName("GetContainerMetrics")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/settings/metrics", async (MetricsSettingsService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(ct)))
            .WithName("GetMetricsSettings");

        api.MapPut("/settings/metrics", async (UpdateMetricsSettingsRequest request, MetricsSettingsService service, CancellationToken ct) =>
                ToResponse(await service.UpdateAsync(
                    new MetricsSettings(
                        request.MinuteRetentionDays, request.HourRetentionDays, request.AlertsEnabled, request.HostCpuPercent,
                        request.HostMemoryPercent, request.DiskPercent, request.ContainerMemoryPercent, request.AlertMinutes),
                    ct)))
            .WithName("UpdateMetricsSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    public static MetricsRange ParseRange(string? range) => range switch
    {
        null or "" or "1h" => MetricsRange.OneHour,
        "15m" => MetricsRange.FifteenMinutes,
        "6h" => MetricsRange.SixHours,
        "24h" or "1d" => MetricsRange.OneDay,
        "7d" => MetricsRange.OneWeek,
        "30d" => MetricsRange.OneMonth,
        "90d" => MetricsRange.ThreeMonths,
        _ => throw new DomainValidationException("The range must be one of 15m, 1h, 6h, 24h, 7d, 30d, 90d."),
    };

    private static MetricsWindowResponse ToResponse(MetricsWindow window) => new(
        window.Resolution, DateTimeOffset.FromUnixTimeSeconds(window.From), DateTimeOffset.FromUnixTimeSeconds(window.To), window.StepSeconds);

    private static ContainerMetricsResponse ToResponse(ContainerUsage metrics) =>
        new(ToResponse(metrics.Window), metrics.Series is { } s ? ToResponse(s) : null, ToResponse(metrics.Deployments));

    private static StackMetricsResponse ToResponse(StackMetrics metrics) => new(
        ToResponse(metrics.Window),
        metrics.Containers.Select(ToResponse).ToList(),
        metrics.Volumes.Select(v => new VolumeUsageResponse(v.Name, L(v.SizeBytes))).ToList(),
        ToResponse(metrics.Deployments));

    private static EnvironmentMetricsResponse ToResponse(EnvironmentMetrics metrics) => new(
        ToResponse(metrics.Window),
        metrics.Source,
        metrics.IntervalSeconds,
        metrics.Host is { } host ? ToResponse(host) : null,
        (metrics.Filesystems ?? [])
            .GroupBy(f => f.MountPoint, StringComparer.Ordinal)
            .Select(g => new FilesystemSeriesResponse(g.Key, g.Select(p => p.Start * 1000).ToList(), Col(g, p => p.Used), Col(g, p => p.Total)))
            .ToList(),
        metrics.Stacks.Select(ToResponse).ToList(),
        metrics.DiskUsage is { } d
            ? new DockerDiskUsageResponse(d.At, L(d.ImagesBytes), L(d.ImagesReclaimableBytes), L(d.ContainersBytes), L(d.VolumesBytes), L(d.BuildCacheBytes))
            : null);

    private static IReadOnlyList<DeploymentMarkerResponse> ToResponse(IReadOnlyList<DeploymentMarker> markers) =>
        markers.Select(m => new DeploymentMarkerResponse(m.Id.Value, m.At, m.Status, m.Commit)).ToList();

    private static ContainerSeriesResponse ToResponse(ContainerSeries series)
    {
        var p = series.Points;
        return new ContainerSeriesResponse(
            series.Name, series.Stack, series.Service, p.Select(x => x.Start * 1000).ToList(),
            Col(p, x => x.CpuAvg), Col(p, x => x.CpuMax), Col(p, x => x.CpuLimit), Col(p, x => x.ThrottledAvg),
            Col(p, x => x.MemoryAvg), Col(p, x => x.MemoryMax), Col(p, x => x.MemoryLimit), Col(p, x => x.CacheAvg),
            Col(p, x => x.RxAvg), Col(p, x => x.TxAvg), Col(p, x => x.ReadAvg), Col(p, x => x.WriteAvg), Col(p, x => x.PidsMax));
    }

    private static HostSeriesResponse ToResponse(IReadOnlyList<HostPoint> p) => new(
        p.Select(x => x.Start * 1000).ToList(),
        Col(p, x => x.CpuCount), Col(p, x => x.CpuUser), Col(p, x => x.CpuSystem), Col(p, x => x.CpuIowait), Col(p, x => x.CpuSteal),
        Col(p, x => x.CpuBusyMax), Col(p, x => x.Load1), Col(p, x => x.MemoryUsedAvg), Col(p, x => x.MemoryUsedMax),
        Col(p, x => x.MemoryTotal), Col(p, x => x.SwapUsed), Col(p, x => x.SwapTotal), Col(p, x => x.DiskRead), Col(p, x => x.DiskWrite),
        Col(p, x => x.NetworkRx), Col(p, x => x.NetworkTx));

    private static CurrentMetricsResponse ToResponse(CurrentMetrics metrics) => new(
        metrics.Source,
        metrics.IntervalSeconds,
        metrics.At,
        metrics.Host is { } h
            ? new CurrentHostResponse(
                h.HasRates, (int)h.CpuCount, h.CpuBusy, h.CpuSteal, h.Load1, h.Load5, h.Load15, L(h.MemoryTotalBytes), L(h.MemoryUsedBytes),
                L(h.SwapTotalBytes), L(h.SwapUsedBytes), h.DiskReadBytesPerSecond, h.DiskWriteBytesPerSecond, h.NetworkRxBytesPerSecond,
                h.NetworkTxBytesPerSecond, h.UptimeSeconds,
                h.Filesystems.Select(f => new CurrentFilesystemResponse(f.MountPoint, f.Device, f.Type, L(f.TotalBytes), L(f.UsedBytes), L(f.AvailableBytes), f.UsedRatio)).ToList())
            : null,
        metrics.Containers.Select(c => new CurrentContainerResponse(
            c.Id, c.Name, c.StackName, c.ServiceName, c.HasRates, c.CpuCores, c.CpuLimitCores, c.CpuThrottledRatio,
            L(c.MemoryWorkingSetBytes), L(c.MemoryCacheBytes), L(c.MemoryLimitBytes), c.NetworkRxBytesPerSecond, c.NetworkTxBytesPerSecond,
            c.HostNetwork, c.BlockReadBytesPerSecond, c.BlockWriteBytesPerSecond, (int)Math.Min(c.Pids, int.MaxValue))).ToList());

    private static MetricsSettingsResponse ToResponse(ResolvedSettings settings)
    {
        SettingTextResponse Text(string field)
        {
            var setting = settings[field];
            return new SettingTextResponse(setting.Value, setting.Source, setting.IsLocked, setting.Problem);
        }

        return new MetricsSettingsResponse(
            Text(MetricsSettingsSection.MinuteRetentionDaysField),
            Text(MetricsSettingsSection.HourRetentionDaysField),
            Text(MetricsSettingsSection.AlertsEnabledField),
            Text(MetricsSettingsSection.HostCpuPercentField),
            Text(MetricsSettingsSection.HostMemoryPercentField),
            Text(MetricsSettingsSection.DiskPercentField),
            Text(MetricsSettingsSection.ContainerMemoryPercentField),
            Text(MetricsSettingsSection.AlertMinutesField));
    }

    // JSON clients read 64-bit unsigned numbers badly (OpenAPI has no format for them); no byte count comes near long.MaxValue.
    private static long L(ulong value) => (long)Math.Min(value, long.MaxValue);

    // Three decimals are plenty for graphs and keep the JSON small.
    private static List<double> Col<T>(IEnumerable<T> points, Func<T, double> value) =>
        points.Select(p => Math.Round(value(p), 3)).ToList();
}
