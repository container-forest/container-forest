using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>
/// Turns an event into notifications: matches it against the active rules, records one notification per
/// matching channel and wakes the bot's stream, which delivers them.
/// </summary>
public sealed class NotificationDispatcher(
    INotificationRuleRepository rules,
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    NotificationFeed feed,
    TimeProvider time)
{
    /// <param name="summary">One line of plain text for the history and the message; no secret values.</param>
    /// <returns>The notifications created, one per channel; empty when no rule matches.</returns>
    public async Task<IReadOnlyList<Notification>> DispatchAsync(
        NotificationEvent notificationEvent, string summary, CancellationToken cancellationToken)
    {
        notificationEvent.EnsureValid();
        var now = time.GetUtcNow();

        // Several rules may send the event to the same channel; it gets one notification, credited to the
        // most specific rule, which is the one the history screen should point at.
        var created = (await rules.ListActiveAsync(cancellationToken))
            .Where(rule => rule.Matches(notificationEvent))
            .OrderByDescending(Specificity)
            .DistinctBy(rule => rule.ChannelId)
            .Select(rule => Notification.Create(rule, notificationEvent, summary, now))
            .ToList();
        if (created.Count == 0)
        {
            return created;
        }

        foreach (var notification in created)
        {
            notifications.Add(notification);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        feed.Publish();
        return created;
    }

    private static int Specificity(NotificationRule rule) =>
        rule.StackId is not null ? 2 : rule.EnvironmentId is not null ? 1 : 0;
}
