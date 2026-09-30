using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Notifications;

/// <summary>
/// What can be notified about. Flags, so a rule can subscribe to several kinds; an event has exactly one.
/// </summary>
[Flags]
public enum NotificationEventKinds
{
    None = 0,
    DeploymentSucceeded = 1 << 0,
    DeploymentFailed = 1 << 1,

    /// <summary>A container stopped without anyone asking it to (Docker "die" not caused by a stop command).</summary>
    ContainerDied = 1 << 2,

    ContainerUnhealthy = 1 << 3,
    AgentOffline = 1 << 4,
    AgentOnline = 1 << 5,

    All = DeploymentSucceeded | DeploymentFailed | ContainerDied | ContainerUnhealthy | AgentOffline | AgentOnline,
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Something that happened and may be worth a notification. Rules decide where it goes
/// (<see cref="NotificationRule.Matches"/>); the text is written when the notification is created.
/// </summary>
/// <param name="StackId">Null for events not tied to a managed stack, e.g. an agent going offline.</param>
public sealed record NotificationEvent(
    NotificationEventKinds Kind,
    EnvironmentId EnvironmentId,
    StackId? StackId,
    DateTimeOffset OccurredAt)
{
    public NotificationSeverity Severity => Kind switch
    {
        NotificationEventKinds.DeploymentFailed or NotificationEventKinds.ContainerDied or NotificationEventKinds.AgentOffline
            => NotificationSeverity.Error,
        NotificationEventKinds.ContainerUnhealthy => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info,
    };

    /// <summary>Throws unless <see cref="Kind"/> is exactly one known kind.</summary>
    public NotificationEvent EnsureSingleKind() =>
        Kind != NotificationEventKinds.None
        && (Kind & (Kind - 1)) == 0
        && (Kind & ~NotificationEventKinds.All) == 0
            ? this
            : throw new DomainValidationException($"A notification event has exactly one kind, not '{Kind}'.");
}
