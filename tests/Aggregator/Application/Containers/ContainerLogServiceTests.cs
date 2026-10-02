using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Containers;

public class ContainerLogServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly ContainerLogService service;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);

    public ContainerLogServiceTests()
    {
        service = new ContainerLogService(new CurrentActor(currentUser, users), environments, stacks, roleAssignments, agent);
        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(environment);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Viewer_reads_the_log_of_a_container_found_by_name()
    {
        Grant(Role.Viewer);
        AddContainer("c1", "web-app-1");
        agent.LogLines.Add(new ContainerLogLine(Now, LogStream.Stdout, "listening on :80"));

        var lines = await service.ReadAsync(environment.Id, "web-app-1", tailLines: null, since: null, Ct);

        Assert.Equal("listening on :80", Assert.Single(lines).Text);
        Assert.Equal(["list", "logs c1"], agent.Calls);
        Assert.Equal(new ContainerLogQuery(ContainerLogService.DefaultTailLines, null), agent.LastLogQuery);
    }

    [Fact]
    public async Task Tail_and_since_reach_the_agent()
    {
        Grant(Role.Viewer);
        AddContainer("c1", "web-app-1");

        await service.ReadAsync(environment.Id, "c1", tailLines: 50, since: Now, Ct);

        Assert.Equal(new ContainerLogQuery(50, Now), agent.LastLogQuery);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ContainerLogService.MaxTailLines + 1)]
    public async Task Tail_out_of_range_is_rejected_before_the_agent_is_asked(int tail)
    {
        Grant(Role.Viewer);
        AddContainer("c1", "web-app-1");

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ReadAsync(environment.Id, "c1", tail, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Private_environment_without_a_role_looks_missing_and_the_agent_is_not_contacted()
    {
        AddContainer("c1", "web-app-1");

        await Assert.ThrowsAsync<NotFoundException>(() => service.ReadAsync(environment.Id, "c1", null, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Stack_role_gives_the_logs_of_that_stack_only()
    {
        var web = AddStack("web");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Viewer, Now));
        AddContainer("c1", "web-app-1", stack: "web");
        AddContainer("c2", "db-1", stack: "db");
        AddContainer("c3", "watchtower");

        await service.ReadAsync(environment.Id, "c1", null, null, Ct);
        await Assert.ThrowsAsync<NotFoundException>(() => service.ReadAsync(environment.Id, "c2", null, null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ReadAsync(environment.Id, "c3", null, null, Ct));

        Assert.Equal(["logs c1"], agent.Calls.Where(c => c.StartsWith("logs", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unknown_container_is_not_found()
    {
        Grant(Role.Viewer);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ReadAsync(environment.Id, "missing", null, null, Ct));
    }

    private void Grant(Role role) => roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));

    private Stack AddStack(string name)
    {
        var stack = Stack.Create(
            environment.Id, StackName.Parse(name), RepositoryId.New(), BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        stacks.Add(stack);
        return stack;
    }

    private void AddContainer(string id, string name, string? stack = null) =>
        agent.Containers.Add(new ContainerSnapshot(id, name, ContainerState.Running, "nginx:1.27", stack, stack is null ? null : "app", "", null));
}
