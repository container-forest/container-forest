using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Metrics;

/// <summary>
/// Every minute: writes the finished minutes of <see cref="MetricsBuffer"/>, builds the hour points of hours that have
/// ended, and once an hour removes what is past the retention of the Metrics settings. A failed write puts the minutes
/// back for the next run; the buffer only grows by one batch per environment and minute meanwhile.
/// </summary>
internal sealed partial class MetricsWriter(
    MetricsBuffer buffer,
    IMetricsStore store,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<MetricsWriter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

    private long rolledUpTo;
    private DateTimeOffset? lastRetention;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>One run of the loop.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var batches = buffer.TakeFinishedMinutes();
        try
        {
            await store.AppendAsync(batches, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            buffer.Return(batches);
            LogWriteFailed(ex, batches.Count);
            return;
        }

        var now = time.GetUtcNow();
        try
        {
            // The hour before the current one is complete once its last minute is written, a minute after it ended.
            // After a restart the previous two hours are built again, which is harmless.
            var currentHour = now.ToUnixTimeSeconds() / 3600 * 3600;
            var from = rolledUpTo > 0 ? rolledUpTo : currentHour - 7200;
            if (from < currentHour)
            {
                await store.RollUpHoursAsync(from, currentHour, cancellationToken);
                rolledUpTo = currentHour;
            }

            if (lastRetention is null || now - lastRetention >= RetentionInterval)
            {
                lastRetention = now;
                await RemoveExpiredAsync(now, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMaintenanceFailed(ex);
        }
    }

    private async Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = MetricsSettings.From(
            await scope.ServiceProvider.GetRequiredService<SettingsStore>().ResolveAsync(MetricsSettingsSection.Instance, cancellationToken));
        var environments = (await scope.ServiceProvider.GetRequiredService<IEnvironmentRepository>().ListAsync(cancellationToken))
            .Select(e => e.Id)
            .ToList();

        var removed = await store.RemoveExpiredAsync(
            now.AddDays(-settings.MinuteRetentionDays).ToUnixTimeSeconds(),
            now.AddDays(-settings.HourRetentionDays).ToUnixTimeSeconds(),
            environments,
            cancellationToken);
        if (removed > 0)
        {
            LogRemoved(removed);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store {Count} minute(s) of resource usage; retrying in a minute")]
    private partial void LogWriteFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not roll up or expire resource usage")]
    private partial void LogMaintenanceFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} expired resource usage points")]
    private partial void LogRemoved(int count);
}
