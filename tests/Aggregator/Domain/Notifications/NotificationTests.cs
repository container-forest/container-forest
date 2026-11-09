using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Notifications;

public class NotificationRuleTests
{
    private readonly NotificationChannel channel = NotificationChannel.CreateDiscord("ops", 123, Now);

    [Fact]
    public void Rule_for_everything_matches_its_kinds_anywhere()
    {
        var rule = NotificationRule.ForEverything(
            channel, NotificationEventKinds.DeploymentFailed | NotificationEventKinds.AgentOffline, Now);

        Assert.True(rule.Matches(Event(NotificationEventKinds.DeploymentFailed, EnvironmentId.New(), StackId.New())));
        Assert.True(rule.Matches(Event(NotificationEventKinds.AgentOffline, EnvironmentId.New(), null)));
        Assert.False(rule.Matches(Event(NotificationEventKinds.DeploymentSucceeded, EnvironmentId.New(), StackId.New())));
    }

    [Fact]
    public void Environment_rule_ignores_other_environments()
    {
        var environment = Environment();
        var rule = NotificationRule.ForEnvironment(channel, environment, NotificationEventKinds.All, Now);

        Assert.True(rule.Matches(Event(NotificationEventKinds.AgentOffline, environment.Id, null)));
        Assert.True(rule.Matches(Event(NotificationEventKinds.ContainerDied, environment.Id, StackId.New())));
        Assert.False(rule.Matches(Event(NotificationEventKinds.AgentOffline, EnvironmentId.New(), null)));
    }

    [Fact]
    public void Stack_rule_ignores_other_stacks_and_environment_wide_events()
    {
        var environment = Environment();
        var stack = Stack(environment.Id);
        var rule = NotificationRule.ForStack(channel, stack, NotificationEventKinds.All, Now);

        Assert.True(rule.Matches(Event(NotificationEventKinds.DeploymentFailed, environment.Id, stack.Id)));
        Assert.False(rule.Matches(Event(NotificationEventKinds.DeploymentFailed, environment.Id, StackId.New())));
        Assert.False(rule.Matches(Event(NotificationEventKinds.AgentOffline, environment.Id, null)));
    }

    [Fact]
    public void Disabled_rule_matches_nothing()
    {
        var rule = NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now);
        rule.Disable();

        Assert.False(rule.Matches(Event(NotificationEventKinds.DeploymentFailed, EnvironmentId.New(), null)));
    }

    [Theory]
    [InlineData(NotificationEventKinds.None)]
    [InlineData((NotificationEventKinds)(1 << 12))]
    public void Rule_needs_known_kinds(NotificationEventKinds events) =>
        Assert.Throws<DomainValidationException>(() => NotificationRule.ForEverything(channel, events, Now));

    [Fact]
    public void Event_has_exactly_one_kind()
    {
        var rule = NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now);
        var both = Event(NotificationEventKinds.DeploymentFailed | NotificationEventKinds.AgentOffline, EnvironmentId.New(), null);

        Assert.Throws<DomainValidationException>(() => rule.Matches(both));
    }

    [Theory]
    [InlineData(NotificationEventKinds.DeploymentFailed, NotificationSeverity.Error)]
    [InlineData(NotificationEventKinds.ContainerUnhealthy, NotificationSeverity.Warning)]
    [InlineData(NotificationEventKinds.DeploymentSucceeded, NotificationSeverity.Info)]
    public void Severity_follows_the_kind(NotificationEventKinds kind, NotificationSeverity severity) =>
        Assert.Equal(severity, Event(kind, EnvironmentId.New(), null).Severity);

    internal static NotificationEvent Event(NotificationEventKinds kind, EnvironmentId environmentId, StackId? stackId) =>
        new(kind, environmentId, stackId, Now);
}

public class NotificationTests
{
    private readonly NotificationRule rule =
        NotificationRule.ForEverything(NotificationChannel.CreateDiscord("ops", 123, Now), NotificationEventKinds.All, Now);

    [Fact]
    public void Created_from_a_matching_rule_as_pending()
    {
        var notification = Create();

        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(rule.ChannelId, notification.ChannelId);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
    }

    [Fact]
    public void Rule_that_does_not_match_cannot_create_one()
    {
        rule.Disable();

        Assert.Throws<DomainConflictException>(() => Create());
    }

    [Fact]
    public void Finishes_once()
    {
        var notification = Create();

        notification.MarkDelivered(Now);

        Assert.Equal(NotificationStatus.Delivered, notification.Status);
        Assert.Throws<DomainConflictException>(() => notification.MarkFailed("late", Now));
    }

    [Fact]
    public void Failure_keeps_a_short_reason()
    {
        var notification = Create();

        notification.MarkFailed(new string('x', Notification.MaxErrorLength + 10), Now);

        Assert.Equal(Notification.MaxErrorLength, notification.Error!.Length);
    }

    [Fact]
    public void Old_pending_notification_is_stale()
    {
        var notification = Create();

        Assert.False(notification.IsStale(Now + Notification.MaxPendingAge - TimeSpan.FromSeconds(1)));
        Assert.True(notification.IsStale(Now + Notification.MaxPendingAge));
    }

    [Fact]
    public void Channel_needs_a_Discord_channel_id() =>
        Assert.Throws<DomainValidationException>(() => NotificationChannel.CreateDiscord("ops", 0, Now));

    private Notification Create() => Notification.Create(
        rule,
        NotificationRuleTests.Event(NotificationEventKinds.DeploymentFailed, EnvironmentId.New(), null),
        "Deployment of web on prod failed",
        Now);
}
