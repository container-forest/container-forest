using ContainerForest.Aggregator.Application.Images;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Images;

/// <summary>Runs <see cref="ImageUpdatePoller"/> once a minute; the poller decides which policies are due.</summary>
internal sealed partial class ImageUpdateScheduler(
    ImageUpdatePoller poller,
    TimeProvider time,
    ILogger<ImageUpdateScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await poller.RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not poll the image policies")]
    private partial void LogFailed(Exception exception);
}
