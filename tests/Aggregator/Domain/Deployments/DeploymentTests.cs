using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Deployments;

public class DeploymentTests
{
    private static readonly CommitSha Commit = CommitSha.Parse(new string('a', 40));

    [Fact]
    public void Goes_through_the_happy_path()
    {
        var deployment = Deployment.QueueManual(StackId.New(), UserId.New(), ImagePullPolicy.Always, Now);

        deployment.Start(Now.AddSeconds(1));
        deployment.ResolveCommit(Commit);
        deployment.Succeed(Now.AddSeconds(31));

        Assert.Equal(DeploymentStatus.Succeeded, deployment.Status);
        Assert.True(deployment.IsFinished);
        Assert.Equal(TimeSpan.FromSeconds(30), deployment.Duration);
    }

    [Fact]
    public void Webhook_deployment_has_no_user_and_pulls_missing_images()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);

        Assert.Null(deployment.RequestedBy);
        Assert.Equal(ImagePullPolicy.Missing, deployment.PullPolicy);
    }

    [Fact]
    public void Cannot_succeed_without_a_commit()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);
        deployment.Start(Now);

        Assert.Throws<DomainConflictException>(() => deployment.Succeed(Now));
    }

    [Fact]
    public void Cannot_succeed_from_queued()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);

        Assert.Throws<DomainConflictException>(() => deployment.ResolveCommit(Commit));
        Assert.Throws<DomainConflictException>(() => deployment.Succeed(Now));
    }

    [Fact]
    public void Finished_deployment_never_changes()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);
        deployment.Cancel(Now);

        Assert.Throws<DomainConflictException>(() => deployment.Start(Now));
        Assert.Throws<DomainConflictException>(() => deployment.Fail("late error", Now));
        Assert.Throws<DomainConflictException>(() => deployment.Cancel(Now));
        Assert.Equal(DeploymentStatus.Cancelled, deployment.Status);
    }

    [Fact]
    public void Cannot_switch_to_another_commit()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);
        deployment.Start(Now);
        deployment.ResolveCommit(Commit);

        deployment.ResolveCommit(Commit);
        Assert.Throws<DomainConflictException>(() => deployment.ResolveCommit(CommitSha.Parse(new string('b', 40))));
    }

    [Fact]
    public void Truncates_long_errors()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);
        deployment.Fail(new string('x', Deployment.MaxErrorLength + 100), Now);

        Assert.Equal(Deployment.MaxErrorLength, deployment.Error!.Length);
    }

    [Fact]
    public void Log_keeps_lines_in_order()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);

        deployment.AppendLog(DeploymentLogLevel.Info, "first", Now);
        deployment.AppendLog(DeploymentLogLevel.Warning, "second", Now);

        Assert.Equal(["first", "second"], deployment.Log.Select(l => l.Text));
        Assert.Equal([0, 1], deployment.Log.Select(l => l.Sequence));
    }

    [Fact]
    public void Log_stops_at_the_limit_with_one_note()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);

        for (var i = 0; i < Deployment.MaxLogLines + 10; i++)
        {
            deployment.AppendLog(DeploymentLogLevel.Info, $"line {i}", Now);
        }

        Assert.Equal(Deployment.MaxLogLines + 1, deployment.Log.Count);
        Assert.Equal(DeploymentLogLevel.Warning, deployment.Log[^1].Level);
    }

    [Fact]
    public void Log_truncates_long_lines()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);

        deployment.AppendLog(DeploymentLogLevel.Info, new string('x', Deployment.MaxLogLineLength + 1), Now);

        Assert.Equal(Deployment.MaxLogLineLength, deployment.Log[0].Text.Length);
    }

    [Fact]
    public void Log_of_a_finished_deployment_is_closed()
    {
        var deployment = Deployment.QueueFromWebhook(StackId.New(), Now);
        deployment.Fail("boom", Now);

        Assert.Throws<DomainConflictException>(() => deployment.AppendLog(DeploymentLogLevel.Info, "late", Now));
    }
}
