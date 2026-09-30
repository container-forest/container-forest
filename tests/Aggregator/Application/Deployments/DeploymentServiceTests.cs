using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class DeploymentServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeDeploymentQueue queue = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly DeploymentService service;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly Stack stack;

    public DeploymentServiceTests()
    {
        service = new DeploymentService(
            new CurrentActor(currentUser, users),
            environments,
            stacks,
            roleAssignments,
            deployments,
            new FakeUnitOfWork(),
            queue,
            agent,
            new FixedTime(Now));
        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(environment);
        stack = AddStack("web");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editor_queues_a_deployment()
    {
        Grant(Role.Editor);

        var deployment = await service.QueueAsync(stack.Id, ImagePullPolicy.Always, Ct);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal(ImagePullPolicy.Always, deployment.PullPolicy);
        Assert.Equal(alice.Id, deployment.RequestedBy);
        Assert.Equal([deployment.Id], queue.Queued);
    }

    [Fact]
    public async Task Operator_may_not_deploy()
    {
        Grant(Role.Operator);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct));
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task Editor_of_the_stack_alone_may_deploy_it()
    {
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, stack, Role.Editor, Now));

        await service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct);

        Assert.Single(queue.Queued);
    }

    [Fact]
    public async Task Invisible_stack_looks_missing()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(stack.Id, Ct));
    }

    [Fact]
    public async Task Deployment_of_an_invisible_stack_looks_missing()
    {
        var deployment = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployments.Add(deployment);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(deployment.Id, Ct));

        Grant(Role.Viewer);
        Assert.Same(deployment, await service.GetAsync(deployment.Id, Ct));
    }

    [Fact]
    public async Task Offline_agent_is_an_error_and_nothing_is_queued()
    {
        Grant(Role.Editor);
        agent.Online = false;

        await Assert.ThrowsAsync<AgentOfflineException>(() => service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct));
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Only_one_deployment_per_stack_at_a_time()
    {
        Grant(Role.Editor);
        await service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.QueueAsync(stack.Id, ImagePullPolicy.Missing, Ct));

        var other = AddStack("db");
        await service.QueueAsync(other.Id, ImagePullPolicy.Missing, Ct);
    }

    [Fact]
    public async Task System_stack_is_not_deployed_directly()
    {
        Grant(Role.Editor);
        var system = AddStack("forest", isSystem: true);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.QueueAsync(system.Id, ImagePullPolicy.Missing, Ct));
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        Grant(Role.Viewer);
        var older = Deployment.QueueManual(stack.Id, alice.Id, ImagePullPolicy.Missing, Now.AddHours(-1));
        var newer = Deployment.QueueManual(stack.Id, alice.Id, ImagePullPolicy.Missing, Now);
        deployments.Add(older);
        deployments.Add(newer);

        var history = await service.ListAsync(stack.Id, Ct);

        Assert.Equal([newer.Id, older.Id], history.Select(d => d.Id));
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
