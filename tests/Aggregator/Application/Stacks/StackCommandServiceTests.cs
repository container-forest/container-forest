using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Stacks;

public class StackCommandServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly StackCommandService service;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly Stack stack;

    public StackCommandServiceTests()
    {
        service = new StackCommandService(
            new CurrentActor(currentUser, users), environments, stacks, roleAssignments, deployments, agent);
        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(environment);
        stack = AddStack("web");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Operator_restarts_the_stack_and_gets_the_compose_output()
    {
        Grant(Role.Operator);
        agent.DeployOutput.AddRange(["Container web-app-1 Restarting", "Container web-app-1 Started"]);

        var result = await service.ExecuteAsync(stack.Id, StackAction.Restart, TimeSpan.FromSeconds(30), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["Container web-app-1 Restarting", "Container web-app-1 Started"], result.Output);
        Assert.Equal(["restart-stack web"], agent.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), agent.LastStopTimeout);
    }

    [Fact]
    public async Task Default_stop_timeout_is_used_when_none_is_given()
    {
        Grant(Role.Editor);

        await service.ExecuteAsync(stack.Id, StackAction.Down, stopTimeout: null, Ct);

        Assert.Equal(["down-stack web"], agent.Calls);
        Assert.Equal(StackCommand.DefaultStopTimeout, agent.LastStopTimeout);
    }

    [Fact]
    public async Task Operator_may_restart_but_not_take_the_stack_down()
    {
        Grant(Role.Operator);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(stack.Id, StackAction.Down, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Viewer_may_do_neither()
    {
        Grant(Role.Viewer);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(stack.Id, StackAction.Down, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Operator_of_the_stack_alone_may_restart_it()
    {
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, stack, Role.Operator, Now));

        await service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct);

        Assert.Equal(["restart-stack web"], agent.Calls);
    }

    [Fact]
    public async Task Invisible_stack_looks_missing_and_the_agent_is_not_contacted()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(StackId.New(), StackAction.Restart, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task System_stack_cannot_be_taken_down_or_restarted_directly()
    {
        Grant(Role.Editor);
        var system = AddStack("forest", isSystem: true);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ExecuteAsync(system.Id, StackAction.Down, null, Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => service.ExecuteAsync(system.Id, StackAction.Restart, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Stack_with_a_deployment_in_progress_is_a_conflict()
    {
        Grant(Role.Editor);
        deployments.Add(Deployment.QueueManual(stack.Id, alice.Id, ImagePullPolicy.Missing, Now));

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public async Task Stop_timeout_out_of_range_is_rejected(int seconds)
    {
        Grant(Role.Editor);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.ExecuteAsync(stack.Id, StackAction.Restart, TimeSpan.FromSeconds(seconds), Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Failed_compose_run_is_a_result_with_its_output()
    {
        Grant(Role.Editor);
        agent.DeployOutput.Add("Error response from daemon: conflict");
        agent.DeployError = new AgentCommandFailedException(
            environment.Id, AgentErrorKind.CommandFailed, "docker compose down exited with code 1; see the command output.");

        var result = await service.ExecuteAsync(stack.Id, StackAction.Down, null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("docker compose down exited with code 1; see the command output.", result.Error);
        Assert.Equal(["Error response from daemon: conflict"], result.Output);
    }

    [Fact]
    public async Task Stack_the_agent_never_deployed_stays_an_error()
    {
        Grant(Role.Operator);
        agent.DeployError = new AgentCommandFailedException(environment.Id, AgentErrorKind.NotFound, "Stack 'web' is not deployed.");

        var error = await Assert.ThrowsAsync<AgentCommandFailedException>(
            () => service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct));
        Assert.Equal(AgentErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task Offline_agent_is_an_error()
    {
        Grant(Role.Operator);
        agent.Online = false;

        await Assert.ThrowsAsync<AgentOfflineException>(() => service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct));
    }

    [Fact]
    public async Task Caller_cancellation_does_not_reach_a_started_compose_run()
    {
        Grant(Role.Editor);

        await service.ExecuteAsync(stack.Id, StackAction.Down, null, Ct);

        Assert.False(agent.LastStackTokenCancellable);
    }

    [Fact]
    public async Task Only_the_last_output_lines_are_kept()
    {
        Grant(Role.Operator);
        agent.DeployOutput.AddRange(Enumerable.Range(1, StackCommand.MaxOutputLines + 5).Select(i => $"line {i}"));

        var result = await service.ExecuteAsync(stack.Id, StackAction.Restart, null, Ct);

        Assert.Equal(StackCommand.MaxOutputLines, result.Output.Count);
        Assert.Equal("line 6", result.Output[0]);
        Assert.Equal($"line {StackCommand.MaxOutputLines + 5}", result.Output[^1]);
    }

    private void Grant(Role role) => roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));

    private Stack AddStack(string name, bool isSystem = false)
    {
        var created = Stack.Create(
            environment.Id,
            StackName.Parse(name),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem,
            Now);
        stacks.Add(created);
        return created;
    }
}
