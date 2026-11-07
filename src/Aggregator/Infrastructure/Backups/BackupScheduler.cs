using ContainerForest.Aggregator.Application.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Backups;

/// <summary>
/// Looks once a minute whether a backup is due. Settings are read every time, so a change in the panel takes effect
/// without a restart, and the time of the last run comes from the database, so a restart does not make an extra backup.
/// </summary>
internal sealed partial class BackupScheduler(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BackupRunner>().RunIfDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not check whether a backup is due")]
    private partial void LogFailed(Exception exception);
}
