using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class NotificationDispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();

    [Fact]
    public async Task Each_matching_channel_gets_one_notification_from_its_most_specific_rule()
    {
        var ops = fixture.AddChannel("ops");
        var web = fixture.AddChannel("web-team");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, NotificationFixture.Now));
        var stackRule = fixture.AddRule(NotificationRule.ForStack(ops, fixture.Stack, NotificationEventKinds.DeploymentFailed, NotificationFixture.Now));
        var webRule = fixture.AddRule(NotificationRule.ForEnvironment(web, fixture.Environment, NotificationEventKinds.DeploymentFailed, NotificationFixture.Now));

        var created = await fixture.Dispatcher().DispatchAsync(fixture.FailedDeployment(), "Deployment of web failed", Ct);

        Assert.Equal(2, created.Count);
        Assert.Equal(stackRule.Id, Assert.Single(created, n => n.ChannelId == ops.Id).RuleId);
        Assert.Equal(webRule.Id, Assert.Single(created, n => n.ChannelId == web.Id).RuleId);
        Assert.All(created, n => Assert.Equal(NotificationStatus.Pending, n.Status));
        Assert.Equal(2, fixture.Notifications.All.Count);
        Assert.Equal(1, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task Rules_of_other_kinds_disabled_channels_and_other_environments_do_not_match()
    {
        var ops = fixture.AddChannel("ops");
        var muted = fixture.AddChannel("muted");
        muted.Disable();
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.DeploymentSucceeded, NotificationFixture.Now));
        fixture.AddRule(NotificationRule.ForEverything(muted, NotificationEventKinds.All, NotificationFixture.Now));
        var otherEnvironment = Domain.Environments.DockerEnvironment.Create("staging", isPrivate: false, NotificationFixture.Now);
        fixture.AddRule(NotificationRule.ForEnvironment(ops, otherEnvironment, NotificationEventKinds.All, NotificationFixture.Now));

        var created = await fixture.Dispatcher().DispatchAsync(fixture.FailedDeployment(), "Deployment of web failed", Ct);

        Assert.Empty(created);
        Assert.Empty(fixture.Notifications.All);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task New_notifications_wake_the_bot_stream()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, NotificationFixture.Now));
        using var subscription = fixture.Feed.Subscribe(Ct);

        await fixture.Dispatcher().DispatchAsync(fixture.FailedDeployment(), "Deployment of web failed", Ct);

        // A wake-up returns at once instead of waiting for the timeout.
        var wait = subscription.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
    }

    [Fact]
    public async Task Event_with_details_of_another_kind_is_rejected()
    {
        var invalid = fixture.FailedDeployment() with { Kind = NotificationEventKinds.DeploymentSucceeded };

        await Assert.ThrowsAsync<DomainValidationException>(() => fixture.Dispatcher().DispatchAsync(invalid, "Deployed web", Ct));
    }
}
