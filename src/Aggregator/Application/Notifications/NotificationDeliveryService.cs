using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>A pending notification with what the bot needs to post it, resolved at sending time.</summary>
/// <param name="DiscordChannelId">The channel's current Discord channel, so moving a channel also moves its pending notifications.</param>
/// <param name="EnvironmentName">
/// The environment's current name; its id if the environment was deleted since; <see cref="NotificationDeliveryService.SystemName"/>
/// for an event about the whole system, because a bot of an older version needs a name to show.
/// </param>
public sealed record OutgoingNotification(Notification Notification, ulong DiscordChannelId, string EnvironmentName);

/// <summary>
/// The aggregator's side of delivery through the bot (bot.proto, WatchNotifications and ReportDelivery):
/// hands out pending notifications and records what became of them.
/// </summary>
public sealed class NotificationDeliveryService(
    INotificationRepository notifications,
    INotificationChannelRepository channels,
    IEnvironmentRepository environments,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    ILogger<NotificationDeliveryService> logger)
{
    public const string SystemName = "System";
    public const string ChannelDeletedError = "The notification channel was deleted.";
    public const string ChannelDisabledError = "The notification channel was disabled.";
    public const string UnknownBotError = "The bot could not post the notification.";

    /// <summary>
    /// Pending notifications to send, oldest first. Those older than <see cref="Notification.MaxPendingAge"/>
    /// are expired, and those whose channel is gone or disabled are failed, instead of being returned.
    /// </summary>
    /// <param name="awaitingReport">Already sent on this stream; they are not sent twice while the bot is posting them.</param>
    public async Task<IReadOnlyList<OutgoingNotification>> TakePendingAsync(
        IReadOnlySet<NotificationId> awaitingReport, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var channelCache = new Dictionary<NotificationChannelId, NotificationChannel?>();
        var environmentNames = new Dictionary<EnvironmentId, string>();
        var ready = new List<OutgoingNotification>();
        var finished = 0;

        foreach (var notification in await notifications.ListPendingAsync(cancellationToken))
        {
            if (awaitingReport.Contains(notification.Id))
            {
                continue;
            }

            if (notification.IsStale(now))
            {
                notification.Expire(now);
                finished++;
                continue;
            }

            if (!channelCache.TryGetValue(notification.ChannelId, out var channel))
            {
                channel = await channels.FindAsync(notification.ChannelId, cancellationToken);
                channelCache[notification.ChannelId] = channel;
            }

            if (channel is not { IsEnabled: true })
            {
                notification.MarkFailed(channel is null ? ChannelDeletedError : ChannelDisabledError, now);
                finished++;
                continue;
            }

            string? environmentName;
            if (notification.EnvironmentId is not { } environmentId)
            {
                environmentName = SystemName;
            }
            else if (!environmentNames.TryGetValue(environmentId, out environmentName))
            {
                var environment = await environments.FindAsync(environmentId, cancellationToken);
                environmentName = environment?.Name ?? environmentId.ToString();
                environmentNames[environmentId] = environmentName;
            }

            ready.Add(new OutgoingNotification(notification, channel.DiscordChannelId, environmentName));
        }

        if (finished > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Finished {Count} notification(s) that cannot be delivered any more", finished);
        }

        return ready;
    }

    /// <summary>Records the bot's report. A report for a notification that is already finished, e.g. expired meanwhile, is ignored.</summary>
    /// <param name="error">Why posting failed, shown in the panel; ignored when <paramref name="delivered"/>.</param>
    /// <exception cref="NotFoundException">No such notification.</exception>
    public async Task ReportAsync(NotificationId id, bool delivered, string? error, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetAsync(id, cancellationToken);
        if (notification.IsFinished)
        {
            logger.LogDebug("Ignoring a delivery report for notification {NotificationId}, which is already {Status}", id, notification.Status);
            return;
        }

        var now = time.GetUtcNow();
        if (delivered)
        {
            notification.MarkDelivered(now);
        }
        else
        {
            notification.MarkFailed(string.IsNullOrWhiteSpace(error) ? UnknownBotError : error.Trim(), now);
            logger.LogWarning("The bot could not post notification {NotificationId}: {Error}", id, notification.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
