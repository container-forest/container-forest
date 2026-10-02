using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Notifications;

/// <summary>
/// Keeps the aggregator's notification stream open, posts every notification in Discord and reports the outcome.
/// Reopens the stream with <see cref="ReconnectBackoff"/> whenever it ends.
/// </summary>
public sealed class NotificationWatcher(
    INotificationSource source,
    INotificationPoster poster,
    ReconnectBackoff backoff,
    TimeProvider time,
    ILogger<NotificationWatcher> logger) : BackgroundService
{
    public const string BotFailureError = "The bot failed while posting the notification; see its log.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await poster.WaitUntilReadyAsync(stoppingToken);
            while (true)
            {
                var opened = time.GetTimestamp();
                await WatchOnceAsync(stoppingToken);

                var delay = backoff.Next(time.GetElapsedTime(opened));
                logger.LogInformation("Reopening the notification stream in {Delay:g}", delay);
                await Task.Delay(delay, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Delivers until the stream ends; failures of the stream itself are logged, not thrown.</summary>
    private async Task WatchOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Watching the aggregator for notifications");
            await foreach (var notification in source.WatchAsync(stoppingToken))
            {
                await DeliverAsync(notification, stoppingToken);
            }

            logger.LogInformation("The aggregator ended the notification stream");
        }
        catch (ForestUnavailableException)
        {
            // GrpcNotificationSource has logged the diagnostic.
        }
        catch (ForestException ex)
        {
            logger.LogWarning("The notification stream failed: {Reason}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug here must not stop the bot's commands along with the host.
            logger.LogError(ex, "The notification stream failed unexpectedly");
        }
    }

    private async Task DeliverAsync(ForestNotification notification, CancellationToken stoppingToken)
    {
        DeliveryResult result;
        try
        {
            result = await poster.PostAsync(notification, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cannot post notification {NotificationId}", notification.Id);
            result = DeliveryResult.Failed(BotFailureError);
        }

        if (!result.IsDelivered)
        {
            logger.LogWarning(
                "Notification {NotificationId} for channel {ChannelId} was not posted: {Error}",
                notification.Id, notification.DiscordChannelId, result.Error);
        }

        try
        {
            await source.ReportAsync(notification.Id, result.IsDelivered, result.Error, stoppingToken);
        }
        catch (ForestException ex)
        {
            // The aggregator sends it again on the next stream; a duplicate message beats a lost one.
            logger.LogWarning("Cannot report notification {NotificationId}: {Reason}", notification.Id, ex.Message);
        }
    }
}
