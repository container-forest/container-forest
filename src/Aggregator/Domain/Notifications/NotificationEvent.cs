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

    /// <summary>A container that was notified as unhealthy passes its health check again.</summary>
    ContainerHealthy = 1 << 6,

    /// <summary>A backup of the aggregator failed. About the whole system: only rules on the whole system get it.</summary>
    BackupFailed = 1 << 7,

    /// <summary>Someone changed the system's settings. About the whole system: only rules on the whole system get it.</summary>
    SettingsChanged = 1 << 8,

    /// <summary>
    /// Someone gained administrator rights or had a password reset by an administrator: both give a way in. About the
    /// whole system: only rules on the whole system get it.
    /// </summary>
    AccountSecurityChanged = 1 << 9,

    /// <summary>
    /// An image policy stopped working (ADR-0015): the push was refused, the compose file no longer has the image, the
    /// tags cannot be ordered, the branch head is not endorsed, or the registry could not be read three times in a row.
    /// </summary>
    ImageUpdateFailed = 1 << 10,

    /// <summary>A user or the image hook pinned an image, or unpinned it: automatic updates of it stop or resume.</summary>
    ImagePinned = 1 << 11,

    /// <summary>
    /// A host's CPU or memory, a disk, or a container's memory against its limit stayed above the threshold of the
    /// Metrics settings for their alert time (ADR-0017).
    /// </summary>
    ResourceUsageHigh = 1 << 12,

    /// <summary>What <see cref="ResourceUsageHigh"/> reported has stayed back under its threshold as long.</summary>
    ResourceUsageNormal = 1 << 13,

    All = DeploymentSucceeded | DeploymentFailed | ContainerDied | ContainerUnhealthy | AgentOffline | AgentOnline
        | ContainerHealthy | BackupFailed | SettingsChanged | AccountSecurityChanged | ImageUpdateFailed | ImagePinned
        | ResourceUsageHigh | ResourceUsageNormal,
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
/// <param name="EnvironmentId">Null for events about the whole system, e.g. a failed backup.</param>
/// <param name="StackId">Null for events not tied to a managed stack, e.g. an agent going offline.</param>
/// <param name="Details">What the event is about, for the message; null when kind and scope say it all.</param>
public sealed record NotificationEvent(
    NotificationEventKinds Kind,
    EnvironmentId? EnvironmentId,
    StackId? StackId,
    DateTimeOffset OccurredAt,
    NotificationDetails? Details = null)
{
    public NotificationSeverity Severity => Kind switch
    {
        NotificationEventKinds.DeploymentFailed or NotificationEventKinds.ContainerDied or NotificationEventKinds.AgentOffline
            or NotificationEventKinds.BackupFailed => NotificationSeverity.Error,
        NotificationEventKinds.ContainerUnhealthy or NotificationEventKinds.SettingsChanged
            or NotificationEventKinds.AccountSecurityChanged or NotificationEventKinds.ImageUpdateFailed
            or NotificationEventKinds.ImagePinned or NotificationEventKinds.ResourceUsageHigh => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info,
    };

    /// <summary>Throws unless <see cref="Kind"/> is exactly one known kind.</summary>
    public NotificationEvent EnsureSingleKind() =>
        Kind != NotificationEventKinds.None
        && (Kind & (Kind - 1)) == 0
        && (Kind & ~NotificationEventKinds.All) == 0
            ? this
            : throw new DomainValidationException($"A notification event has exactly one kind, not '{Kind}'.");

    /// <summary>Throws unless the event has one kind and its <see cref="Details"/>, if any, describe that kind and stack.</summary>
    public NotificationEvent EnsureValid()
    {
        EnsureSingleKind();
        if (Details is not null && !Details.Fits(Kind))
        {
            throw new DomainValidationException($"{Details.GetType().Name} does not describe a {Kind} event.");
        }

        if (Details is DeploymentNotificationDetails deployment && deployment.StackId != StackId)
        {
            throw new DomainValidationException($"The deployment details are of stack {deployment.StackId}, not {StackId}.");
        }

        return this;
    }
}
