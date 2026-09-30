using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Notifications;

/// <summary>
/// Sends events of the chosen kinds from a scope to a channel. The scope is the whole system, one
/// environment, or one stack. Several rules may match one event; each matching channel gets it once.
/// </summary>
public sealed class NotificationRule : Entity<NotificationRuleId>
{
    private NotificationRule(
        NotificationRuleId id,
        NotificationChannelId channelId,
        NotificationEventKinds events,
        EnvironmentId? environmentId,
        StackId? stackId,
        DateTimeOffset createdAt)
        : base(id)
    {
        ChannelId = channelId;
        Events = events;
        EnvironmentId = environmentId;
        StackId = stackId;
        IsEnabled = true;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private NotificationRule() { }

    public NotificationChannelId ChannelId { get; private init; }

    public NotificationEventKinds Events { get; private set; }

    /// <summary>Null: every environment.</summary>
    public EnvironmentId? EnvironmentId { get; private set; }

    /// <summary>Null: every stack of <see cref="EnvironmentId"/>, and events outside stacks. Set only together with it.</summary>
    public StackId? StackId { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static NotificationRule ForEverything(NotificationChannel channel, NotificationEventKinds events, DateTimeOffset now) =>
        new(NotificationRuleId.New(), channel.Id, EnsureEvents(events), null, null, now);

    public static NotificationRule ForEnvironment(
        NotificationChannel channel, DockerEnvironment environment, NotificationEventKinds events, DateTimeOffset now) =>
        new(NotificationRuleId.New(), channel.Id, EnsureEvents(events), environment.Id, null, now);

    public static NotificationRule ForStack(NotificationChannel channel, Stack stack, NotificationEventKinds events, DateTimeOffset now) =>
        new(NotificationRuleId.New(), channel.Id, EnsureEvents(events), stack.EnvironmentId, stack.Id, now);

    public void ChangeEvents(NotificationEventKinds events) => Events = EnsureEvents(events);

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;

    /// <summary>Whether the event goes to this rule's channel.</summary>
    public bool Matches(NotificationEvent notificationEvent)
    {
        if (!IsEnabled || (Events & notificationEvent.EnsureSingleKind().Kind) == 0)
        {
            return false;
        }

        if (EnvironmentId is { } environmentId && environmentId != notificationEvent.EnvironmentId)
        {
            return false;
        }

        // A stack rule ignores events outside that stack, including environment-wide ones like an agent going offline.
        return StackId is not { } stackId || stackId == notificationEvent.StackId;
    }

    private static NotificationEventKinds EnsureEvents(NotificationEventKinds events) =>
        events != NotificationEventKinds.None && (events & ~NotificationEventKinds.All) == 0
            ? events
            : throw new DomainValidationException("A rule must subscribe to at least one known event kind.");
}
