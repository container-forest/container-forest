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
    public void Deploy_on_push_is_off_by_default_and_never_on_for_a_system_stack()
    {
        var stack = Stack(EnvironmentId.New());
        Assert.False(stack.DeployOnPush);
        stack.SetDeployOnPush(true);
        Assert.True(stack.DeployOnPush);

        var system = Stack(EnvironmentId.New(), isSystem: true);
        Assert.Throws<DomainConflictException>(() => system.SetDeployOnPush(true));
        system.SetDeployOnPush(false);
        Assert.False(system.DeployOnPush);
    }

    [Fact]
    public void Deploy_hook_accepts_only_its_token()
    {
        var stack = Stack(EnvironmentId.New());
        Assert.False(stack.HasDeployHook);
        Assert.False(stack.AcceptsDeployHook("token"));

        stack.SetDeployHook(SecretToken.Hash("token"));
        Assert.True(stack.HasDeployHook);
        Assert.True(stack.AcceptsDeployHook("token"));
        Assert.False(stack.AcceptsDeployHook("other"));

        stack.SetDeployHook(SecretToken.Hash("renewed"));
        Assert.False(stack.AcceptsDeployHook("token"));

        stack.RemoveDeployHook();
        Assert.False(stack.AcceptsDeployHook("renewed"));
    }

    [Fact]
    public void Deploy_hook_is_stored_as_a_hash_and_never_set_on_a_system_stack()
    {
        Assert.Throws<DomainValidationException>(() => Stack(EnvironmentId.New()).SetDeployHook("token"));
        Assert.Throws<DomainValidationException>(() => Stack(EnvironmentId.New()).SetDeployHook(SecretToken.Hash("t").ToUpperInvariant()));
        Assert.Throws<DomainConflictException>(() => Stack(EnvironmentId.New(), isSystem: true).SetDeployHook(SecretToken.Hash("t")));
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

        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now), withSecrets: false);

        Assert.Equal(CommitA, stack.DeployedCommit);
    }

    [Fact]
    public void Older_deployment_does_not_overwrite_a_newer_one()
    {
        var stack = Stack(EnvironmentId.New());
        var older = SucceededDeployment(stack, CommitA, Now);
        var newer = SucceededDeployment(stack, CommitB, Now.AddMinutes(1));

        stack.RecordDeployment(newer, withSecrets: false);
        stack.RecordDeployment(older, withSecrets: false);

        Assert.Equal(CommitB, stack.DeployedCommit);
    }

    [Fact]
    public void Taking_the_stack_down_forgets_its_commit_until_the_next_deployment()
    {
        var stack = Stack(EnvironmentId.New());
        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now), withSecrets: false);

        stack.RecordTakenDown();

        Assert.Null(stack.DeployedCommit);
        Assert.Null(stack.DeployedAt);

        stack.RecordDeployment(SucceededDeployment(stack, CommitB, Now.AddMinutes(1)), withSecrets: false);
        Assert.Equal(CommitB, stack.DeployedCommit);
    }

    [Fact]
    public void Only_a_deployed_stack_with_secrets_needs_rehydration()
    {
        var stack = Stack(EnvironmentId.New());
        Assert.False(stack.NeedsRehydration);

        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now), withSecrets: false);
        Assert.False(stack.NeedsRehydration);

        stack.RecordDeployment(SucceededDeployment(stack, CommitB, Now.AddMinutes(1)), withSecrets: true);
        Assert.True(stack.NeedsRehydration);

        stack.RecordTakenDown();
        Assert.False(stack.NeedsRehydration);
        Assert.False(stack.DeployedWithSecrets);
    }

    [Fact]
    public void System_stack_is_never_rehydrated_by_the_pipeline()
    {
        var stack = Stack(EnvironmentId.New(), isSystem: true);

        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now), withSecrets: true);

        Assert.False(stack.NeedsRehydration);
    }

    [Fact]
    public void System_stack_cannot_record_being_taken_down()
    {
        var stack = Stack(EnvironmentId.New(), isSystem: true);
        stack.RecordDeployment(SucceededDeployment(stack, CommitA, Now), withSecrets: false);

        Assert.Throws<DomainConflictException>(stack.RecordTakenDown);
        Assert.Equal(CommitA, stack.DeployedCommit);
    }

    [Fact]
    public void Rejects_a_failed_deployment_or_one_of_another_stack()
    {
        var stack = Stack(EnvironmentId.New());
        var failed = Deployment.QueueFromWebhook(stack.Id, Now);
        failed.Fail("boom", Now);
        var foreign = SucceededDeployment(Stack(stack.EnvironmentId, "other"), CommitA, Now);

        Assert.Throws<DomainConflictException>(() => stack.RecordDeployment(failed, withSecrets: false));
        Assert.Throws<DomainConflictException>(() => stack.RecordDeployment(foreign, withSecrets: false));
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
