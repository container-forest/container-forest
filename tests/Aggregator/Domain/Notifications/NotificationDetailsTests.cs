using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Notifications;

public class NotificationDetailsTests
{
    private static readonly CommitSha Commit = CommitSha.Parse("0123456789abcdef0123456789abcdef01234567");

    private readonly Stack stack = Stack(EnvironmentId.New());

    [Fact]
    public void Succeeded_deployment_has_its_commit_and_no_error()
    {
        var deployment = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Always, Now);
        deployment.Start(Now);
        deployment.ResolveCommit(Commit);
        deployment.Succeed(Now);

        var details = DeploymentNotificationDetails.Of(deployment, stack);

        Assert.True(details.Succeeded);
        Assert.Equal(Commit, details.Commit);
        Assert.Equal(stack.Name, details.StackName);
        Assert.True(details.PulledImages);
        Assert.True(details.Fits(NotificationEventKinds.DeploymentSucceeded));
        Assert.False(details.Fits(NotificationEventKinds.DeploymentFailed));
    }

    [Fact]
    public void Failed_deployment_keeps_the_reason_even_without_a_commit()
    {
        var deployment = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Start(Now);
        deployment.Fail("Branch 'main' does not exist.", Now);

        var details = DeploymentNotificationDetails.Of(deployment, stack);

        Assert.False(details.Succeeded);
        Assert.Null(details.Commit);
        Assert.Equal("Branch 'main' does not exist.", details.Error);
        Assert.True(details.Fits(NotificationEventKinds.DeploymentFailed));
    }

    [Fact]
    public void Unfinished_or_cancelled_deployment_is_not_notified_about()
    {
        var running = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        var cancelled = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        cancelled.Cancel(Now);

        Assert.Throws<DomainConflictException>(() => DeploymentNotificationDetails.Of(running, stack));
        Assert.Throws<DomainConflictException>(() => DeploymentNotificationDetails.Of(cancelled, stack));
    }

    [Fact]
    public void Deployment_of_another_stack_is_rejected()
    {
        var deployment = Deployment.QueueManual(StackId.New(), UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Fail("failed", Now);

        Assert.Throws<DomainValidationException>(() => DeploymentNotificationDetails.Of(deployment, stack));
    }

    [Fact]
    public void Event_details_must_describe_its_kind_and_stack()
    {
        var details = new DeploymentNotificationDetails(DeploymentId.New(), stack.Id, stack.Name, Commit, false, Error: null);

        var valid = new NotificationEvent(NotificationEventKinds.DeploymentSucceeded, stack.EnvironmentId, stack.Id, Now, details);
        var wrongKind = valid with { Kind = NotificationEventKinds.DeploymentFailed };
        var wrongStack = valid with { StackId = StackId.New() };

        Assert.Same(valid, valid.EnsureValid());
        Assert.Throws<DomainValidationException>(() => wrongKind.EnsureValid());
        Assert.Throws<DomainValidationException>(() => wrongStack.EnsureValid());
    }

    [Fact]
    public void Notification_keeps_the_details()
    {
        var channel = NotificationChannel.CreateDiscord("ops", 123, Now);
        var rule = NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now);
        var details = new DeploymentNotificationDetails(DeploymentId.New(), stack.Id, stack.Name, null, false, "failed");
        var notificationEvent = new NotificationEvent(NotificationEventKinds.DeploymentFailed, stack.EnvironmentId, stack.Id, Now, details);

        var notification = Notification.Create(rule, notificationEvent, "Deployment of web failed", Now);

        Assert.Equal(details, notification.Details);
    }

    [Fact]
    public void Notification_rejects_details_of_another_kind()
    {
        var channel = NotificationChannel.CreateDiscord("ops", 123, Now);
        var rule = NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now);
        var details = new DeploymentNotificationDetails(DeploymentId.New(), stack.Id, stack.Name, null, false, "failed");
        var notificationEvent = new NotificationEvent(NotificationEventKinds.AgentOffline, stack.EnvironmentId, stack.Id, Now, details);

        Assert.Throws<DomainValidationException>(() => Notification.Create(rule, notificationEvent, "prod is offline", Now));
    }
}
