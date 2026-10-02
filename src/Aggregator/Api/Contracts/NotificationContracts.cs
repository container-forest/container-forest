using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Api.Contracts;

// Notification channels, their rules and the notification history for the panel. Administrators only.
//
//   GET    /api/notification-channels               -> NotificationChannelResponse[] (name order)
//   POST   /api/notification-channels               -> 201 NotificationChannelResponse
//   PUT    /api/notification-channels/{id}          -> NotificationChannelResponse
//   DELETE /api/notification-channels/{id}          -> 204; its rules go too, its history stays
//   GET    /api/notification-channels/{id}/rules    -> NotificationRuleResponse[] (oldest first)
//   POST   /api/notification-channels/{id}/rules    -> 201 NotificationRuleResponse
//   PUT    /api/notification-rules/{id}             -> NotificationRuleResponse; events and on/off, the scope stays
//   DELETE /api/notification-rules/{id}             -> 204
//   GET    /api/notifications                       -> NotificationResponse[] (newest first); ?limit=1..500, default 100
//
// Discord ids are strings: they are 64-bit numbers, and JavaScript numbers lose their last digits.
// Events are lists of single kinds (DeploymentSucceeded, DeploymentFailed, ...); at least one.

/// <param name="DiscordChannelId">The Discord text channel the bot posts to.</param>
/// <param name="IsEnabled">A disabled channel receives nothing; its rules stay.</param>
public sealed record NotificationChannelResponse(
    Guid Id, string Name, NotificationChannelKind Kind, string DiscordChannelId, bool IsEnabled, DateTimeOffset CreatedAt);

public sealed record CreateNotificationChannelRequest(string Name, string DiscordChannelId);

public sealed record UpdateNotificationChannelRequest(string Name, string DiscordChannelId, bool IsEnabled);

/// <param name="EnvironmentId">Null: every environment.</param>
/// <param name="StackId">Null: every stack of the environment, and events outside stacks (e.g. an agent going offline).</param>
public sealed record NotificationRuleResponse(
    Guid Id,
    Guid ChannelId,
    IReadOnlyList<NotificationEventKinds> Events,
    Guid? EnvironmentId,
    string? EnvironmentName,
    Guid? StackId,
    string? StackName,
    bool IsEnabled,
    DateTimeOffset CreatedAt);

/// <param name="EnvironmentId">Null with a null stack: the whole system.</param>
/// <param name="StackId">One stack; its environment may be left out.</param>
public sealed record CreateNotificationRuleRequest(IReadOnlyList<NotificationEventKinds> Events, Guid? EnvironmentId, Guid? StackId);

public sealed record UpdateNotificationRuleRequest(IReadOnlyList<NotificationEventKinds> Events, bool IsEnabled);

/// <param name="ChannelName">Null when the channel has been deleted since.</param>
/// <param name="Error">Why delivery failed, e.g. the bot may not post in the channel.</param>
public sealed record NotificationResponse(
    Guid Id,
    Guid ChannelId,
    string? ChannelName,
    NotificationEventKinds Kind,
    NotificationSeverity Severity,
    Guid EnvironmentId,
    Guid? StackId,
    DateTimeOffset OccurredAt,
    string Summary,
    NotificationStatus Status,
    DateTimeOffset? FinishedAt,
    string? Error);
