using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Api.Contracts;

// Resource usage (ADR-0017). Visibility is that of containers: a container of a managed stack needs a role on the
// stack or its environment; the host, unmanaged stacks and containers outside compose need a role on the whole
// environment (every public environment gives each user one as a Viewer).
//   GET /api/environments/{id}/metrics/current                       -> CurrentMetricsResponse; the latest sample
//   GET /api/environments/{id}/metrics?range=1h                      -> EnvironmentMetricsResponse
//   GET /api/stacks/{id}/metrics?range=1h                            -> StackMetricsResponse
//   GET /api/environments/{id}/unmanaged-stacks/{name}/metrics       -> StackMetricsResponse
//   GET /api/environments/{id}/containers/{container}/metrics        -> ContainerMetricsResponse; {container} as for logs,
//                                                                       or the name of a container that is gone
//   GET /api/settings/metrics                                        -> MetricsSettingsResponse; administrators
//   PUT /api/settings/metrics                                        -> MetricsSettingsResponse; administrators; audited
//
// range is one of 15m, 1h (default), 6h, 24h, 7d, 30d, 90d. An hour or less comes from the samples in memory (every
// agent interval, 10 s by default), longer ranges from stored minutes or hours in buckets of window.stepSeconds.
// Series are columns: at[i] (Unix milliseconds) belongs to every other array's [i]. CPU is in cores (1.5 = one and a
// half cores), host CPU in shares of all cores (0..1), memory in bytes, rates in bytes per second. A limit of 0 means
// none. *Max arrays are the highest of shorter periods in the bucket; for raw samples they equal the averages.

public sealed record MetricsWindowResponse(MetricsResolution Resolution, DateTimeOffset From, DateTimeOffset To, long StepSeconds);

public sealed record ContainerSeriesResponse(
    string Name,
    string Stack,
    string Service,
    IReadOnlyList<long> At,
    IReadOnlyList<double> Cpu,
    IReadOnlyList<double> CpuMax,
    IReadOnlyList<double> CpuLimit,
    IReadOnlyList<double> CpuThrottled,
    IReadOnlyList<double> Memory,
    IReadOnlyList<double> MemoryMax,
    IReadOnlyList<double> MemoryLimit,
    IReadOnlyList<double> MemoryCache,
    IReadOnlyList<double> NetworkRx,
    IReadOnlyList<double> NetworkTx,
    IReadOnlyList<double> BlockRead,
    IReadOnlyList<double> BlockWrite,
    IReadOnlyList<double> Pids);

public sealed record HostSeriesResponse(
    IReadOnlyList<long> At,
    IReadOnlyList<double> CpuCount,
    IReadOnlyList<double> CpuUser,
    IReadOnlyList<double> CpuSystem,
    IReadOnlyList<double> CpuIowait,
    IReadOnlyList<double> CpuSteal,
    IReadOnlyList<double> CpuBusyMax,
    IReadOnlyList<double> Load1,
    IReadOnlyList<double> MemoryUsed,
    IReadOnlyList<double> MemoryUsedMax,
    IReadOnlyList<double> MemoryTotal,
    IReadOnlyList<double> SwapUsed,
    IReadOnlyList<double> SwapTotal,
    IReadOnlyList<double> DiskRead,
    IReadOnlyList<double> DiskWrite,
    IReadOnlyList<double> NetworkRx,
    IReadOnlyList<double> NetworkTx);

public sealed record FilesystemSeriesResponse(string MountPoint, IReadOnlyList<long> At, IReadOnlyList<double> Used, IReadOnlyList<double> Total);

public sealed record DeploymentMarkerResponse(Guid Id, DateTimeOffset At, DeploymentStatus Status, string? Commit);

public sealed record ContainerMetricsResponse(
    MetricsWindowResponse Window, ContainerSeriesResponse? Series, IReadOnlyList<DeploymentMarkerResponse> Deployments);

public sealed record VolumeUsageResponse(string Name, ulong SizeBytes);

public sealed record StackMetricsResponse(
    MetricsWindowResponse Window,
    IReadOnlyList<ContainerSeriesResponse> Containers,
    IReadOnlyList<VolumeUsageResponse> Volumes,
    IReadOnlyList<DeploymentMarkerResponse> Deployments);

/// <param name="Source">How the agent reads usage; Unsupported for an agent too old for metrics.</param>
/// <param name="Host">Null without a role on the whole environment.</param>
/// <param name="Stacks">Sums per compose project, Name = the project; "" holds containers outside compose.</param>
/// <param name="DiskUsage">docker system df, refreshed about every 15 minutes; null before the first one.</param>
public sealed record EnvironmentMetricsResponse(
    MetricsWindowResponse Window,
    MetricsSourceKind Source,
    int IntervalSeconds,
    HostSeriesResponse? Host,
    IReadOnlyList<FilesystemSeriesResponse> Filesystems,
    IReadOnlyList<ContainerSeriesResponse> Stacks,
    DockerDiskUsageResponse? DiskUsage);

public sealed record DockerDiskUsageResponse(
    DateTimeOffset At, ulong ImagesBytes, ulong ImagesReclaimableBytes, ulong ContainersBytes, ulong VolumesBytes, ulong BuildCacheBytes);

public sealed record CurrentMetricsResponse(
    MetricsSourceKind Source,
    int IntervalSeconds,
    DateTimeOffset? At,
    CurrentHostResponse? Host,
    IReadOnlyList<CurrentContainerResponse> Containers);

/// <param name="HasRates">False right after the agent connected: CPU, disk and network are not known yet.</param>
public sealed record CurrentHostResponse(
    bool HasRates,
    uint CpuCount,
    double CpuBusy,
    double CpuSteal,
    double Load1,
    double Load5,
    double Load15,
    ulong MemoryTotalBytes,
    ulong MemoryUsedBytes,
    ulong SwapTotalBytes,
    ulong SwapUsedBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double NetworkRxBytesPerSecond,
    double NetworkTxBytesPerSecond,
    long UptimeSeconds,
    IReadOnlyList<CurrentFilesystemResponse> Filesystems);

public sealed record CurrentFilesystemResponse(string MountPoint, string Device, string Type, ulong TotalBytes, ulong UsedBytes, ulong AvailableBytes, double UsedRatio);

public sealed record CurrentContainerResponse(
    string Id,
    string Name,
    string? Stack,
    string? Service,
    bool HasRates,
    double CpuCores,
    double CpuLimitCores,
    double CpuThrottledRatio,
    ulong MemoryBytes,
    ulong MemoryCacheBytes,
    ulong MemoryLimitBytes,
    double NetworkRxBytesPerSecond,
    double NetworkTxBytesPerSecond,
    bool HostNetwork,
    double BlockReadBytesPerSecond,
    double BlockWriteBytesPerSecond,
    uint Pids);

public sealed record MetricsSettingsResponse(
    SettingTextResponse MinuteRetentionDays,
    SettingTextResponse HourRetentionDays,
    SettingTextResponse AlertsEnabled,
    SettingTextResponse HostCpuPercent,
    SettingTextResponse HostMemoryPercent,
    SettingTextResponse DiskPercent,
    SettingTextResponse ContainerMemoryPercent,
    SettingTextResponse AlertMinutes);

/// <param name="HostCpuPercent">A threshold of 0 turns that alert off; AlertMinutes is how long it must hold.</param>
public sealed record UpdateMetricsSettingsRequest(
    int MinuteRetentionDays,
    int HourRetentionDays,
    bool AlertsEnabled,
    int HostCpuPercent,
    int HostMemoryPercent,
    int DiskPercent,
    int ContainerMemoryPercent,
    int AlertMinutes);
