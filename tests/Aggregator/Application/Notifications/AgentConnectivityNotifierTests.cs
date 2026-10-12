using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class AgentConnectivityNotifierTests
{
    private static readonly DateTimeOffset Now = NotificationFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();

    [Fact]
    public async Task Offline_agent_becomes_an_error_notification_of_its_environment()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEnvironment(ops, fixture.Environment, NotificationEventKinds.AgentOffline, Now));

        await Notifier().NotifyAsync(fixture.Environment.Id, NotificationEventKinds.AgentOffline, Now.AddMinutes(-2), Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationEventKinds.AgentOffline, notification.Kind);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal(fixture.Environment.Id, notification.EnvironmentId);
        Assert.Null(notification.StackId);
        Assert.Null(notification.Details);
        Assert.Equal(Now.AddMinutes(-2), notification.OccurredAt);
        Assert.Equal("The agent of 'prod' went offline and has not reconnected.", notification.Summary);
    }

    [Fact]
    public async Task Agent_back_online_is_an_info_notification()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.AgentOnline, Now));

        await Notifier().NotifyAsync(fixture.Environment.Id, NotificationEventKinds.AgentOnline, Now, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal("The agent of 'prod' is back online.", notification.Summary);
    }

    [Fact]
    public async Task Stack_rule_does_not_hear_about_the_agent()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForStack(ops, fixture.Stack, NotificationEventKinds.All, Now));

        var created = await Notifier().NotifyAsync(fixture.Environment.Id, NotificationEventKinds.AgentOffline, Now, Ct);

        Assert.Empty(created);
        Assert.Empty(fixture.Notifications.All);
    }

    [Fact]
    public async Task Deleted_environment_is_not_notified_about()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));

        var created = await Notifier().NotifyAsync(EnvironmentId.New(), NotificationEventKinds.AgentOffline, Now, Ct);

        Assert.Empty(created);
    }

    [Fact]
    public async Task Other_kinds_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Notifier().NotifyAsync(fixture.Environment.Id, NotificationEventKinds.DeploymentFailed, Now, Ct));
    }

    private AgentConnectivityNotifier Notifier() =>
        new(fixture.Dispatcher(), fixture.Environments, NullLogger<AgentConnectivityNotifier>.Instance);
}
