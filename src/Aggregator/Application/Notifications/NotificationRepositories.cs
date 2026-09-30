using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Notifications;

public interface INotificationChannelRepository : IRepository<NotificationChannel, NotificationChannelId>
{
    /// <summary>In name order.</summary>
    Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken);
}

public interface INotificationRuleRepository : IRepository<NotificationRule, NotificationRuleId>
{
    /// <summary>Enabled rules of enabled channels: the ones an event is matched against.</summary>
    Task<IReadOnlyList<NotificationRule>> ListActiveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationRule>> ListByChannelAsync(NotificationChannelId channelId, CancellationToken cancellationToken);
}

public interface INotificationRepository : IRepository<Notification, NotificationId>
{
    /// <summary>Oldest first: the order they are delivered in.</summary>
    Task<IReadOnlyList<Notification>> ListPendingAsync(CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Notification>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}
