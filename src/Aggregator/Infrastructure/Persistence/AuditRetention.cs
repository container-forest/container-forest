using ContainerForest.Aggregator.Application.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>
/// Removes audit entries older than <see cref="AuditOptions.RetentionDays"/>: once at start, then once a day. A failed
/// run is only logged; the next one catches up.
/// </summary>
internal sealed partial class AuditRetention(
    IServiceScopeFactory scopes,
    AuditOptions options,
    TimeProvider time,
    ILogger<AuditRetention> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RetentionDays == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await RemoveExpiredAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One run; returns how many entries were removed.</summary>
    internal async Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var log = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            var removed = await log.RemoveOlderThanAsync(time.GetUtcNow().AddDays(-options.RetentionDays), cancellationToken);
            if (removed > 0)
            {
                LogRemoved(removed, options.RetentionDays);
            }

            return removed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} audit entries older than {Days} days")]
    private partial void LogRemoved(int count, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not remove expired audit entries")]
    private partial void LogFailed(Exception exception);
}
