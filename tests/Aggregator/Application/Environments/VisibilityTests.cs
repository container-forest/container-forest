using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Environments;

public class VisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly EnvironmentService environmentService;
    private readonly ContainerQueryService containerService;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment publicEnvironment = DockerEnvironment.Create("staging", isPrivate: false, Now);
    private readonly DockerEnvironment privateEnvironment = DockerEnvironment.Create("prod", isPrivate: true, Now);

    public VisibilityTests()
    {
        var actor = new CurrentActor(currentUser, users);
        environmentService = new EnvironmentService(
            actor, environments, roleAssignments, stacks, unitOfWork, agent, TimeProvider.System);
        containerService = new ContainerQueryService(actor, environments, stacks, roleAssignments, agent);

        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(publicEnvironment);
        environments.Add(privateEnvironment);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Private_environment_is_listed_only_with_a_role_on_it()
    {
        Assert.Equal(["staging"], (await environmentService.ListVisibleAsync(Ct)).Select(e => e.Name));

        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, privateEnvironment.Id, Role.Operator, Now));

        var listed = await environmentService.ListVisibleAsync(Ct);
        Assert.Equal(Role.Operator, listed.Single(e => e.Name == "prod").Role);
        Assert.Equal(Role.Viewer, listed.Single(e => e.Name == "staging").Role);
    }

    [Fact]
    public async Task Stack_role_reveals_the_environment_without_an_environment_role()
    {
        var web = AddStack(privateEnvironment, "web");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Operator, Now));

        var prod = (await environmentService.ListVisibleAsync(Ct)).Single(e => e.Name == "prod");

        Assert.Null(prod.Role);
    }

    [Fact]
    public async Task Stack_role_shows_only_that_stack_s_containers()
    {
        var web = AddStack(privateEnvironment, "web");
        AddStack(privateEnvironment, "db");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Operator, Now));
        AddContainer("c1", "web-app-1", stack: "web");
        AddContainer("c2", "db-1", stack: "db");
        AddContainer("c3", "loose", stack: null);

        var visible = await containerService.ListAsync(privateEnvironment.Id, Ct);

        var container = Assert.Single(visible);
        Assert.Equal("web-app-1", container.Container.Name);
        Assert.True(container.CanControl);
    }

    [Fact]
    public async Task Implicit_viewer_sees_containers_of_a_public_environment_but_cannot_control_them()
    {
        AddContainer("c1", "web-app-1", stack: null);

        var container = Assert.Single(await containerService.ListAsync(publicEnvironment.Id, Ct));

        Assert.False(container.CanControl);
    }

    [Fact]
    public async Task Containers_of_an_invisible_environment_look_missing_and_the_agent_is_not_contacted()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => containerService.ListAsync(privateEnvironment.Id, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Only_administrators_create_environments_and_issue_tokens()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => environmentService.CreateAsync("new", isPrivate: true, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => environmentService.IssueEnrollmentTokenAsync(publicEnvironment.Id, Ct));
        Assert.Equal(0, unitOfWork.Saves);

        // A new request: CurrentActor keeps the user for the lifetime of one request.
        var admin = User.CreateAdministrator("root", Now);
        users.Add(admin);
        var adminService = new EnvironmentService(
            new CurrentActor(new FakeCurrentUser { Id = admin.Id }, users),
            environments, roleAssignments, stacks, unitOfWork, agent, TimeProvider.System);

        var created = await adminService.CreateAsync("new", isPrivate: true, Ct);
        Assert.False(string.IsNullOrEmpty(created.EnrollmentToken.Token));
    }

    private Stack AddStack(DockerEnvironment environment, string name)
    {
        var stack = Stack.Create(
            environment.Id,
            StackName.Parse(name),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem: false,
            Now);
        stacks.Add(stack);
        return stack;
    }

    private void AddContainer(string id, string name, string? stack) =>
        agent.Containers.Add(new ContainerSnapshot(id, name, ContainerState.Running, "nginx:1.27", stack, null, "", null));
}
