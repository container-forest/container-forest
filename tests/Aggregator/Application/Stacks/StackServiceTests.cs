using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Stacks;

public class StackServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryGitRepositories repositories = new();
    private readonly InMemoryGitConnections connections = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly StackService service;
    private readonly RepositoryService repositoryService;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly GitRepository infra = GitRepository.Create("infra", "https://git.example.com/infra.git", null, Now);
    private readonly GitRepository apps = GitRepository.Create("apps", "https://git.example.com/apps.git", null, Now);
    private readonly Stack web;

    public StackServiceTests()
    {
        var actor = new CurrentActor(currentUser, users);
        service = new StackService(
            actor, environments, stacks, repositories, connections, roleAssignments, deployments, unitOfWork, new FixedTime(Now));
        repositoryService = new RepositoryService(
            new RepositoryAccess(actor, roleAssignments, stacks, environments),
            repositories,
            connections,
            new FakeSecretProtector(),
            unitOfWork,
            new FixedTime(Now));
        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(environment);
        repositories.Add(infra);
        repositories.Add(apps);
        web = Stack.Create(
            environment.Id, StackName.Parse("web"), infra.Id, BranchName.Parse("main"), StackFilePath.Parse("web/compose.yaml"), isSystem: false, Now);
        stacks.Add(web);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_comes_with_its_repository_name_and_what_the_user_may_do()
    {
        Grant(Role.Operator);

        var stack = await service.GetAsync(web.Id, Ct);

        Assert.Equal(("web", "infra", environment.Id), (stack.Name.Value, stack.RepositoryName, stack.EnvironmentId));
        Assert.Equal((false, true), (stack.CanDeploy, stack.CanRestart));
    }

    [Fact]
    public async Task Invisible_stack_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(web.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(StackId.New(), Ct));
    }

    [Fact]
    public async Task Editor_changes_the_source()
    {
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Editor, Now));

        var changed = await service.ChangeSourceAsync(web.Id, apps.Id, "release", "deploy/compose.yaml", Ct);

        Assert.Equal(("apps", "release", "deploy/compose.yaml"), (changed.RepositoryName, changed.Branch.Value, changed.ComposePath.Value));
        Assert.Equal((apps.Id, "release"), (web.RepositoryId, web.Branch.Value));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Operator_may_not_change_the_source()
    {
        Grant(Role.Operator);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ChangeSourceAsync(web.Id, apps.Id, "main", "compose.yaml", Ct));
        Assert.Equal(infra.Id, web.RepositoryId);
    }

    [Fact]
    public async Task Source_does_not_change_under_an_unfinished_deployment()
    {
        Grant(Role.Editor);
        deployments.Add(Deployment.QueueManual(web.Id, alice.Id, ImagePullPolicy.Missing, Now));

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ChangeSourceAsync(web.Id, apps.Id, "main", "compose.yaml", Ct));
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Editor_of_the_environment_removes_a_stack_that_is_not_deployed()
    {
        Grant(Role.Editor);
        Assert.True((await service.GetAsync(web.Id, Ct)).CanRemove);

        await service.RemoveAsync(web.Id, Ct);

        Assert.Null(await stacks.FindAsync(web.Id, Ct));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Editor_of_the_stack_alone_may_not_remove_it()
    {
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Editor, Now));
        Assert.False((await service.GetAsync(web.Id, Ct)).CanRemove);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RemoveAsync(web.Id, Ct));
        Assert.NotNull(await stacks.FindAsync(web.Id, Ct));
    }

    [Fact]
    public async Task Deployed_stack_or_one_with_a_deployment_in_progress_is_not_removed()
    {
        Grant(Role.Editor);
        var deployment = Deployment.QueueManual(web.Id, alice.Id, ImagePullPolicy.Missing, Now);
        deployments.Add(deployment);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.RemoveAsync(web.Id, Ct));

        deployment.Start(Now);
        deployment.ResolveCommit(CommitSha.Parse(new string('a', 40)));
        deployment.Succeed(Now);
        web.RecordDeployment(deployment, withSecrets: false);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.RemoveAsync(web.Id, Ct));
        Assert.NotNull(await stacks.FindAsync(web.Id, Ct));
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Invisible_stack_is_not_found_for_removal()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveAsync(web.Id, Ct));
    }

    [Theory]
    [InlineData("main", "../compose.yaml")]
    [InlineData("-bad", "compose.yaml")]
    public async Task Invalid_source_is_rejected(string branch, string composePath)
    {
        Grant(Role.Editor);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ChangeSourceAsync(web.Id, apps.Id, branch, composePath, Ct));
    }

    [Fact]
    public async Task Unknown_repository_is_not_found()
    {
        Grant(Role.Editor);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ChangeSourceAsync(web.Id, RepositoryId.New(), "main", "compose.yaml", Ct));
    }

    [Fact]
    public async Task Editors_list_repositories_and_viewers_do_not()
    {
        Grant(Role.Viewer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => repositoryService.ListAsync(Ct));

        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Editor, Now));
        Assert.Equal(["apps", "infra"], (await repositoryService.ListAsync(Ct)).Select(r => r.Repository.Name));
    }

    [Fact]
    public async Task A_repository_of_a_connection_not_allowed_here_is_not_found()
    {
        Grant(Role.Editor);
        var (_, team) = AddConnectionRepository(allowedHere: false);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateAsync(environment.Id, "api", team.Id, "main", "compose.yaml", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ChangeSourceAsync(web.Id, team.Id, "main", "compose.yaml", Ct));
        Assert.Equal(infra.Id, web.RepositoryId);
    }

    [Fact]
    public async Task A_repository_of_a_connection_allowed_here_is_used()
    {
        Grant(Role.Editor);
        var (_, team) = AddConnectionRepository(allowedHere: true);

        var created = await service.CreateAsync(environment.Id, "api", team.Id, "main", "compose.yaml", Ct);

        Assert.Equal(team.Id, created.RepositoryId);
    }

    private (GitConnection Connection, GitRepository Repository) AddConnectionRepository(bool allowedHere)
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);
        connection.AllowEnvironments(false, allowedHere ? [environment.Id] : [EnvironmentId.New()]);
        connections.Add(connection);
        var repository = GitRepository.Create("team-app", "https://gitea.example.com/team/app.git", connection, Now);
        repositories.Add(repository);
        return (connection, repository);
    }

    private void Grant(Role role) => roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));
}
