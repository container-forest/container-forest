using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class DeploymentNotifierTests
{
    private static readonly DateTimeOffset Now = NotificationFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();

    public DeploymentNotifierTests()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));
    }

    [Fact]
    public async Task Succeeded_deployment_becomes_a_notification_with_its_commit()
    {
        var deployment = Deployment.QueueManual(fixture.Stack.Id, UserId.New(), ImagePullPolicy.Always, Now);
        deployment.Start(Now);
        deployment.ResolveCommit(DeployFixtures.Commit);
        deployment.Succeed(Now.AddMinutes(1));

        await Notifier().DeploymentFinishedAsync(new DeploymentOutcome(deployment, fixture.Stack, fixture.Environment), Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationEventKinds.DeploymentSucceeded, notification.Kind);
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal(fixture.Stack.Id, notification.StackId);
        Assert.Equal(Now.AddMinutes(1), notification.OccurredAt);
        Assert.Equal("Deployed stack 'web' on 'prod' at commit 0123456 with freshly pulled images.", notification.Summary);
        var details = Assert.IsType<DeploymentNotificationDetails>(notification.Details);
        Assert.Equal(deployment.Id, details.DeploymentId);
        Assert.Equal(DeployFixtures.Commit, details.Commit);
    }

    [Fact]
    public async Task Failed_deployment_is_summarized_on_one_line()
    {
        var deployment = Deployment.QueueManual(fixture.Stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Start(Now);
        deployment.Fail("docker compose exited with code 1:\n  service \"db\" is unhealthy\r\n", Now);

        await Notifier().DeploymentFinishedAsync(new DeploymentOutcome(deployment, fixture.Stack, fixture.Environment), Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationEventKinds.DeploymentFailed, notification.Kind);
        Assert.Equal(
            "Deployment of stack 'web' on 'prod' failed: docker compose exited with code 1: service \"db\" is unhealthy",
            notification.Summary);
        Assert.Equal(deployment.Error, Assert.IsType<DeploymentNotificationDetails>(notification.Details).Error);
    }

    [Fact]
    public async Task Cancelled_deployment_is_not_notified_about()
    {
        var deployment = Deployment.QueueManual(fixture.Stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Cancel(Now);

        await Notifier().DeploymentFinishedAsync(new DeploymentOutcome(deployment, fixture.Stack, fixture.Environment), Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Fact]
    public async Task Failure_to_notify_does_not_reach_the_pipeline()
    {
        fixture.Rules.Failure = new InvalidOperationException("database is locked");
        var deployment = Deployment.QueueManual(fixture.Stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Fail("failed", Now);

        await Notifier().DeploymentFinishedAsync(new DeploymentOutcome(deployment, fixture.Stack, fixture.Environment), Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    private DeploymentNotifier Notifier() => new(fixture.Dispatcher(), NullLogger<DeploymentNotifier>.Instance);
}
