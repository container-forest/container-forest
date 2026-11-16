using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>
/// Turns resource usage into <see cref="NotificationEventKinds.ResourceUsageHigh"/> and
/// <see cref="NotificationEventKinds.ResourceUsageNormal"/> (ADR-0017). Watched, each against its threshold of the
/// Metrics settings: the host's CPU (busy share of all cores) and memory, every disk's fullness, and the memory of
/// every container that has a limit, against that limit. A value must stay at or above its threshold for the alert
/// time before "high" goes out, and below it as long before "normal" closes it; one alert is open per subject.
/// </summary>
/// <remarks>
/// The state lives in memory like that of <c>ContainerIncidentWatch</c>: an alert open when the aggregator stops is
/// not closed, and a container that goes away with an open alert keeps it until it comes back.
/// </remarks>
public sealed partial class ResourceAlertWatch(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<ResourceAlertWatch> logger) : IDisposable
{
    private static readonly TimeSpan SettingsRefresh = TimeSpan.FromMinutes(1);

    private readonly Lock gate = new();
    private readonly Dictionary<AlertKey, AlertState> states = [];
    private readonly CancellationTokenSource stopping = new();
    private MetricsSettings settings = MetricsSettings.Default;
    private DateTimeOffset? settingsLoadedAt;
    private bool disposed;

    /// <summary>The settings in use; refreshed in the background about once a minute.</summary>
    public MetricsSettings Settings
    {
        get
        {
            lock (gate)
            {
                return settings;
            }
        }
    }

    /// <returns>The alerts this sample opened or closed; their notifications are sent in the background.</returns>
    public IReadOnlyList<ResourceAlert> Observe(MetricsSnapshot snapshot)
    {
        RefreshSettingsIfDue();
        var raised = new List<ResourceAlert>();
        lock (gate)
        {
            if (disposed)
            {
                return [];
            }

            if (!settings.AlertsEnabled)
            {
                states.Clear();
                return [];
            }

            var seen = new HashSet<AlertKey>();
            foreach (var (key, percent, threshold, stack) in Readings(snapshot, settings))
            {
                seen.Add(key);
                if (Evaluate(key, percent, threshold, snapshot.At) is { } kind)
                {
                    raised.Add(new ResourceAlert(snapshot.EnvironmentId, kind, key.Alert, key.Subject, stack, percent, threshold, snapshot.At));
                }
            }

            // A reading that is no longer reported (a stopped container, an unmounted disk) starts over when it returns.
            foreach (var key in states.Keys.Where(k => k.EnvironmentId == snapshot.EnvironmentId && !seen.Contains(k) && !states[k].Open).ToList())
            {
                states.Remove(key);
            }
        }

        foreach (var alert in raised)
        {
            _ = NotifyAsync(alert);
        }

        return raised;
    }

    /// <summary>Forgets a deleted environment.</summary>
    public void Remove(EnvironmentId environmentId)
    {
        lock (gate)
        {
            foreach (var key in states.Keys.Where(k => k.EnvironmentId == environmentId).ToList())
            {
                states.Remove(key);
            }
        }
    }

    private static IEnumerable<(AlertKey Key, double Percent, int Threshold, string? Stack)> Readings(MetricsSnapshot snapshot, MetricsSettings settings)
    {
        var env = snapshot.EnvironmentId;
        if (snapshot.Host is { } host)
        {
            if (settings.HostCpuPercent > 0 && host.HasRates)
            {
                yield return (new AlertKey(env, ResourceAlertKind.HostCpu, ""), host.CpuBusy * 100, settings.HostCpuPercent, null);
            }

            if (settings.HostMemoryPercent > 0 && host.MemoryTotalBytes > 0)
            {
                yield return (new AlertKey(env, ResourceAlertKind.HostMemory, ""), 100.0 * host.MemoryUsedBytes / host.MemoryTotalBytes, settings.HostMemoryPercent, null);
            }

            if (settings.DiskPercent > 0)
            {
                foreach (var fs in host.Filesystems)
                {
                    yield return (new AlertKey(env, ResourceAlertKind.Disk, fs.MountPoint), fs.UsedRatio * 100, settings.DiskPercent, null);
                }
            }
        }

        if (settings.ContainerMemoryPercent > 0)
        {
            foreach (var c in snapshot.Containers.Where(c => c.MemoryLimitBytes > 0))
            {
                yield return (new AlertKey(env, ResourceAlertKind.ContainerMemory, c.Name), 100.0 * c.MemoryWorkingSetBytes / c.MemoryLimitBytes,
                    settings.ContainerMemoryPercent, c.StackName);
            }
        }
    }

    private NotificationEventKinds? Evaluate(AlertKey key, double percent, int threshold, DateTimeOffset at)
    {
        if (!states.TryGetValue(key, out var state))
        {
            states[key] = state = new AlertState();
        }

        if (percent >= threshold)
        {
            state.BelowSince = null;
            state.AboveSince ??= at;
            if (!state.Open && at - state.AboveSince >= settings.AlertDelay)
            {
                state.Open = true;
                return NotificationEventKinds.ResourceUsageHigh;
            }
        }
        else
        {
            state.AboveSince = null;
            if (state.Open)
            {
                state.BelowSince ??= at;
                if (at - state.BelowSince >= settings.AlertDelay)
                {
                    state.Open = false;
                    state.BelowSince = null;
                    return NotificationEventKinds.ResourceUsageNormal;
                }
            }
        }

        return null;
    }

    private void RefreshSettingsIfDue()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (settingsLoadedAt is { } loaded && now - loaded < SettingsRefresh)
            {
                return;
            }

            settingsLoadedAt = now;
        }

        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var loaded = MetricsSettings.From(
                await scope.ServiceProvider.GetRequiredService<SettingsStore>().ResolveAsync(MetricsSettingsSection.Instance, stopping.Token));
            lock (gate)
            {
                settings = loaded;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSettingsFailed(ex);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NotifyAsync(ResourceAlert alert)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ResourceAlertNotifier>().NotifyAsync(alert, stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogNotifyFailed(ex, alert.Alert, alert.EnvironmentId);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        stopping.Cancel();
        stopping.Dispose();
    }

    private readonly record struct AlertKey(EnvironmentId EnvironmentId, ResourceAlertKind Alert, string Subject);

    private sealed class AlertState
    {
        public DateTimeOffset? AboveSince { get; set; }
        public DateTimeOffset? BelowSince { get; set; }
        public bool Open { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the Metrics settings; alerts keep the previous ones")]
    private partial void LogSettingsFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not notify about {Alert} in environment {EnvironmentId}")]
    private partial void LogNotifyFailed(Exception exception, ResourceAlertKind alert, EnvironmentId environmentId);
}

/// <param name="Subject">The mount point or the container's name; empty for the host's CPU and memory.</param>
/// <param name="StackName">The compose project of a container.</param>
public sealed record ResourceAlert(
    EnvironmentId EnvironmentId,
    NotificationEventKinds Kind,
    ResourceAlertKind Alert,
    string Subject,
    string? StackName,
    double Percent,
    int ThresholdPercent,
    DateTimeOffset At);
