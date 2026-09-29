using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Stacks;

public class StackTests
{
    private static readonly CommitSha CommitA = CommitSha.Parse(new string('a', 40));
    private static readonly CommitSha CommitB = CommitSha.Parse(new string('b', 40));

    [Theory]
    [InlineData(StackOperation.Down)]
    [InlineData(StackOperation.Remove)]
    public void System_stack_cannot_be_taken_down_or_removed(StackOperation operation)
    {
        var stack = Stack(EnvironmentId.New(), isSystem: true);

        Assert.Throws<DomainConflictException>(() => stack.EnsureAllows(operation));
    }

    [Theory]
    [InlineData(StackOperation.Restart)]
    [InlineData(StackOperation.Redeploy)]
    [InlineData(StackOperation.RedeployWithPull)]
    public void System_stack_updates_go_through_the_helper(StackOperation operation)
    {
        Assert.True(Stack(EnvironmentId.New(), isSystem: true).RequiresUpdateHelper(operation));
        Assert.False(Stack(EnvironmentId.New()).RequiresUpdateHelper(operation));
    }

    [Fact]
    public void Regular_stack_allows_every_operation()
    {
        var stack = Stack(EnvironmentId.New());

        foreach (var operation in Enum.GetValues<StackOperation>())
        {
            stack.EnsureAllows(operation);
        }
    }

    [Fact]
    public void Records_the_commit_of_a_successful_deployment()
    {
        var stack = Stack(EnvironmentId.New());

        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now));

        Assert.Equal(CommitA, stack.DeployedCommit);
    }

    [Fact]
    public void Older_deployment_does_not_overwrite_a_newer_one()
    {
        var stack = Stack(EnvironmentId.New());
        var older = SucceededDeployment(stack, CommitA, Now);
        var newer = SucceededDeployment(stack, CommitB, Now.AddMinutes(1));

        stack.RecordDeployment(newer);
        stack.RecordDeployment(older);

        Assert.Equal(CommitB, stack.DeployedCommit);
    }

    [Fact]
    public void Rejects_a_failed_deployment_or_one_of_another_stack()
    {
        var stack = Stack(EnvironmentId.New());
        var failed = Deployment.QueueFromWebhook(stack.Id, Now);
        failed.Fail("boom", Now);
        var foreign = SucceededDeployment(Stack(stack.EnvironmentId, "other"), CommitA, Now);

        Assert.Throws<DomainConflictException>(() => stack.RecordDeployment(failed));
        Assert.Throws<DomainConflictException>(() => stack.RecordDeployment(foreign));
    }

    private static Deployment SucceededDeployment(Stack stack, CommitSha commit, DateTimeOffset finishedAt)
    {
        var deployment = Deployment.QueueFromWebhook(stack.Id, Now);
        deployment.Start(Now);
        deployment.ResolveCommit(commit);
        deployment.Succeed(finishedAt);
        return deployment;
    }
}
