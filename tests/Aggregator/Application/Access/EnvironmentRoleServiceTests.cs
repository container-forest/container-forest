using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Access;

/// <summary>Roles on one environment, managed by its environment administrator (ADR-0012).</summary>
public class EnvironmentRoleServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User owner = User.Create("owner", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly DockerEnvironment other = DockerEnvironment.Create("staging", isPrivate: true, Now);
    private readonly Stack stack;

    public EnvironmentRoleServiceTests()
    {
        users.Add(admin);
        users.Add(owner);
        users.Add(alice);
        environments.Add(environment);
        environments.Add(other);
        stack = Stack.Create(
            environment.Id, StackName.Parse("web"), RepositoryId.New(), BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"), false, Now);
        stacks.Add(stack);
        roleAssignments.Add(RoleAssignment.ForEnvironment(owner.Id, environment.Id, Role.EnvironmentAdmin, Now));
        currentUser.Id = owner.Id;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EnvironmentRoleService Service()
    {
        var actor = new CurrentActor(currentUser, users);
        return new EnvironmentRoleService(
            new EnvironmentAdministration(actor, environments, roleAssignments),
            users, roleAssignments, stacks, unitOfWork, new FakeAuditTrail(), new FixedTime(Now));
    }

    [Fact]
    public async Task Environment_administrator_gives_roles_on_the_environment_and_its_stacks()
    {
        var onStack = await Service().GrantAsync(environment.Id, "alice", stack.Id, Role.Editor, Ct);
        var changed = await Service().ChangeAsync(environment.Id, onStack.Id, Role.Operator, Ct);

        Assert.Equal(("alice", "web", Role.Operator), (changed.UserName, changed.StackName, changed.Role));
        var listed = await Service().ListAsync(environment.Id, Ct);
        Assert.Equal(["alice", "owner"], listed.Select(v => v.UserName));

        await Service().RevokeAsync(environment.Id, onStack.Id, Ct);
        Assert.Single(await Service().ListAsync(environment.Id, Ct));
    }

    [Fact]
    public async Task Only_administrators_give_or_take_the_environment_administrator_role()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().GrantAsync(environment.Id, "alice", null, Role.EnvironmentAdmin, Ct));
        var ownRole = Assert.Single(await Service().ListAsync(environment.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().ChangeAsync(environment.Id, ownRole.Id, Role.Editor, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().RevokeAsync(environment.Id, ownRole.Id, Ct));

        currentUser.Id = admin.Id;
        var granted = await Service().GrantAsync(environment.Id, "alice", null, Role.EnvironmentAdmin, Ct);
        Assert.Equal(Role.EnvironmentAdmin, granted.Role);
    }

    [Fact]
    public async Task Environment_administrator_role_is_not_given_on_a_stack()
    {
        currentUser.Id = admin.Id;

        await Assert.ThrowsAsync<DomainValidationException>(() => Service().GrantAsync(environment.Id, "alice", stack.Id, Role.EnvironmentAdmin, Ct));
    }

    [Fact]
    public async Task Other_environments_stay_out_of_reach()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ListAsync(other.Id, Ct));

        roleAssignments.Add(RoleAssignment.ForEnvironment(owner.Id, other.Id, Role.Editor, Now));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().GrantAsync(other.Id, "alice", null, Role.Viewer, Ct));
    }

    [Fact]
    public async Task Editor_does_not_administer_the_environment()
    {
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, Role.Editor, Now));
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().ListAsync(environment.Id, Ct));
    }

    [Fact]
    public async Task Read_only_token_of_the_environment_administrator_administers_nothing()
    {
        currentUser.Scope = new AccessScope(null, readOnly: true);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().ListAsync(environment.Id, Ct));
    }
}
