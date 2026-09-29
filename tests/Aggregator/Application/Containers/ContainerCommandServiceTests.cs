using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Containers;

public class ContainerCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly FakeAgentChannel agent = new();
    private readonly ContainerCommandService service;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);

    public ContainerCommandServiceTests()
    {
        service = new ContainerCommandService(users, environments, stacks, roleAssignments, agent);
        users.Add(alice);
        environments.Add(environment);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Operator_on_the_environment_restarts_a_container()
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await Run(ContainerAction.Restart, "c1");

        Assert.Equal(["list", "restart c1"], agent.Calls);
        Assert.Equal(StoppingContainerCommand.DefaultStopTimeout, agent.LastStopTimeout);
    }

    [Fact]
    public async Task Container_can_be_addressed_by_name()
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", ContainerState.Exited);

        await Run(ContainerAction.Start, "web-app-1");

        Assert.Contains("start c1", agent.Calls);
    }

    [Fact]
    public async Task Viewer_is_denied_and_nothing_is_sent()
    {
        Grant(Role.Viewer);
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(ContainerAction.Stop, "c1"));
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stack_role_applies_to_containers_of_that_stack_only()
    {
        var web = AddStack("web");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Operator, Now));
        AddContainer("c1", "web-app-1", ContainerState.Running, stack: "web");
        AddContainer("c2", "db-1", ContainerState.Running, stack: "db");

        await Run(ContainerAction.Restart, "c1");
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(ContainerAction.Restart, "c2"));
    }

    [Fact]
    public async Task Private_environment_without_a_role_looks_missing_and_the_agent_is_not_contacted()
    {
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await Assert.ThrowsAsync<NotFoundException>(() => Run(ContainerAction.Restart, "c1"));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Admin_can_act_without_any_role()
    {
        var admin = User.CreateAdministrator("root", Now);
        users.Add(admin);
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await service.ExecuteAsync(admin.Id, environment.Id, "c1", ContainerAction.Stop, stopTimeout: null, Ct);

        Assert.Contains("stop c1", agent.Calls);
    }

    [Fact]
    public async Task Deactivated_user_is_denied()
    {
        Grant(Role.Editor);
        alice.Deactivate();
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(ContainerAction.Restart, "c1"));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Unknown_container_is_not_found()
    {
        Grant(Role.Operator);

        await Assert.ThrowsAsync<NotFoundException>(() => Run(ContainerAction.Restart, "missing"));
    }

    [Theory]
    [InlineData(ContainerAction.Start, ContainerState.Running)]
    [InlineData(ContainerAction.Start, ContainerState.Paused)]
    [InlineData(ContainerAction.Stop, ContainerState.Exited)]
    [InlineData(ContainerAction.Stop, ContainerState.Created)]
    [InlineData(ContainerAction.Restart, ContainerState.Dead)]
    [InlineData(ContainerAction.Restart, ContainerState.Removing)]
    public async Task Action_that_makes_no_sense_in_the_current_state_is_a_conflict(ContainerAction action, ContainerState state)
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", state);

        await Assert.ThrowsAsync<DomainConflictException>(() => Run(action, "c1"));
        Assert.Equal(["list"], agent.Calls);
    }

    [Fact]
    public async Task Unknown_state_is_left_to_docker()
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", ContainerState.Unknown);

        await Run(ContainerAction.Stop, "c1");

        Assert.Contains("stop c1", agent.Calls);
    }

    [Theory]
    [InlineData(ContainerAction.Stop, ContainerState.Running)]
    [InlineData(ContainerAction.Restart, ContainerState.Running)]
    public async Task System_stack_containers_cannot_be_stopped_or_restarted(ContainerAction action, ContainerState state)
    {
        Grant(Role.Editor);
        AddStack("forest", isSystem: true);
        AddContainer("c1", "forest-agent-1", state, stack: "forest");

        await Assert.ThrowsAsync<DomainConflictException>(() => Run(action, "c1"));
    }

    [Fact]
    public async Task System_stack_container_can_be_started()
    {
        Grant(Role.Editor);
        AddStack("forest", isSystem: true);
        AddContainer("c1", "forest-aggregator-1", ContainerState.Exited, stack: "forest");

        await Run(ContainerAction.Start, "c1");

        Assert.Contains("start c1", agent.Calls);
    }

    [Fact]
    public async Task Custom_stop_timeout_reaches_the_agent()
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await service.ExecuteAsync(alice.Id, environment.Id, "c1", ContainerAction.Stop, TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(TimeSpan.FromSeconds(30), agent.LastStopTimeout);
    }

    [Fact]
    public async Task Stop_timeout_out_of_range_is_rejected()
    {
        Grant(Role.Operator);
        AddContainer("c1", "web-app-1", ContainerState.Running);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.ExecuteAsync(alice.Id, environment.Id, "c1", ContainerAction.Stop, TimeSpan.FromHours(1), Ct));
    }

    private Task Run(ContainerAction action, string containerId) =>
        service.ExecuteAsync(alice.Id, environment.Id, containerId, action, stopTimeout: null, Ct);

    private void Grant(Role role) => roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));

    private Stack AddStack(string name, bool isSystem = false)
    {
        var stack = Stack.Create(
            environment.Id,
            StackName.Parse(name),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem,
            Now);
        stacks.Add(stack);
        return stack;
    }

    private void AddContainer(string id, string name, ContainerState state, string? stack = null) =>
        agent.Containers.Add(new ContainerSnapshot(id, name, state, "nginx:1.27", stack, stack is null ? null : "app", "", null));
}
