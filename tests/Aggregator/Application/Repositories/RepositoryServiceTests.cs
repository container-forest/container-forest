using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Repositories;

public class RepositoryServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryGitRepositories repositories = new();
    private readonly InMemoryGitConnections connections = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeSecretProtector protector = new();
    private readonly RepositoryService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment production = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly DockerEnvironment staging = DockerEnvironment.Create("staging", isPrivate: true, Now);
    private readonly GitConnection team = GitConnection.Create("team", "https://gitea.example.com/team/", Now);
    private readonly GitConnection billing = GitConnection.Create("billing", "https://gitea.example.com/billing/", Now);
    private readonly GitRepository app;

    public RepositoryServiceTests()
    {
        var actor = new CurrentActor(currentUser, users);
        service = new RepositoryService(
            new RepositoryAccess(actor, roleAssignments, stacks, environments),
            repositories,
            connections,
            protector,
            unitOfWork,
            new FixedTime(Now));
        users.Add(admin);
        users.Add(alice);
        environments.Add(production);
        environments.Add(staging);
        team.AllowEnvironments(false, [production.Id]);
        billing.AllowEnvironments(false, [staging.Id]);
        connections.Add(team);
        connections.Add(billing);
        app = GitRepository.Create("app", "https://gitea.example.com/team/app.git", team, Now);
        repositories.Add(app);
        currentUser.Id = alice.Id;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editor_adds_a_repository_under_a_connection_of_their_environment_without_a_token()
    {
        Grant(production, Role.Editor);

        var added = await service.CreateAsync("api", "https://gitea.example.com/team/api.git", team.Id, Ct);

        Assert.Equal((team.Id, true), (added.Repository.ConnectionId, added.CanManage));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Connection_of_other_environments_is_not_found_for_an_editor()
    {
        Grant(production, Role.Editor);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateAsync("ledger", "https://gitea.example.com/billing/ledger.git", billing.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.UpdateAsync(app.Id, "app", "https://gitea.example.com/billing/ledger.git", billing.Id, Ct));
        Assert.Equal(team.Id, app.ConnectionId);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Url_outside_the_connection_s_prefix_is_rejected()
    {
        Grant(production, Role.Editor);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync("ledger", "https://gitea.example.com/billing/ledger.git", team.Id, Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAsync(app.Id, "app", "https://gitea.example.com/billing/ledger.git", team.Id, Ct));
    }

    [Fact]
    public async Task Stack_editor_may_add_a_public_repository()
    {
        var web = AddStack(staging, app.Id);
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Editor, Now));

        var added = await service.CreateAsync("tools", "https://github.com/team/tools.git", null, Ct);

        Assert.Null(added.Connection);
    }

    [Fact]
    public async Task Users_without_an_editor_role_work_with_no_repositories()
    {
        Grant(production, Role.Operator);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.CreateAsync("tools", "https://github.com/team/tools.git", null, Ct));
    }

    [Fact]
    public async Task Editor_lists_public_repositories_and_those_of_their_connections()
    {
        Grant(production, Role.Editor);
        repositories.Add(GitRepository.Create("ledger", "https://gitea.example.com/billing/ledger.git", billing, Now));
        repositories.Add(GitRepository.Create("tools", "https://github.com/team/tools.git", null, Now));

        var listed = await service.ListAsync(Ct);

        Assert.Equal(["app", "tools"], listed.Select(r => r.Repository.Name));
    }

    [Fact]
    public async Task Administrator_lists_and_manages_every_repository()
    {
        currentUser.Id = admin.Id;
        repositories.Add(GitRepository.Create("ledger", "https://gitea.example.com/billing/ledger.git", billing, Now));

        var listed = await service.ListAsync(Ct);

        Assert.Equal(["app", "ledger"], listed.Select(r => r.Repository.Name));
        Assert.All(listed, r => Assert.True(r.CanManage));
    }

    [Fact]
    public async Task Changing_a_repository_takes_the_right_to_deploy_each_of_its_stacks()
    {
        var mine = AddStack(production, app.Id, "mine");
        AddStack(production, app.Id, "theirs");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, mine, Role.Editor, Now));

        var listed = Assert.Single(await service.ListAsync(Ct));
        Assert.False(listed.CanManage);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.UpdateAsync(app.Id, "app", "https://gitea.example.com/team/other.git", team.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateWebhookSecretAsync(app.Id, Ct));

        Grant(production, Role.Editor);
        var updated = await service.UpdateAsync(app.Id, "App", "https://gitea.example.com/team/other.git", team.Id, Ct);
        Assert.Equal(("App", "/team/other.git"), (updated.Repository.Name, updated.Repository.Url.AbsolutePath));
    }

    [Fact]
    public async Task Repository_is_made_public_by_dropping_its_connection()
    {
        Grant(production, Role.Editor);

        var updated = await service.UpdateAsync(app.Id, "app", "https://github.com/team/app.git", null, Ct);

        Assert.Null(updated.Repository.ConnectionId);
    }

    [Fact]
    public async Task Repository_of_a_connection_the_user_may_not_use_is_not_found()
    {
        Grant(staging, Role.Editor);

        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateWebhookSecretAsync(app.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveWebhookSecretAsync(RepositoryId.New(), Ct));
    }

    [Fact]
    public async Task Webhook_secret_is_shown_once_and_stored_encrypted()
    {
        Grant(production, Role.Editor);

        var (repository, secret) = await service.CreateWebhookSecretAsync(app.Id, Ct);

        Assert.True(repository.Repository.HasWebhook);
        Assert.Equal(secret, protector.Unprotect(SecretPurpose.WebhookSecret, app.ProtectedWebhookSecret!));

        await service.RemoveWebhookSecretAsync(app.Id, Ct);
        Assert.False(app.HasWebhook);
    }

    [Fact]
    public async Task Names_need_not_be_unique()
    {
        Grant(production, Role.Editor);

        await service.CreateAsync("app", "https://gitea.example.com/team/app2.git", team.Id, Ct);

        Assert.Equal(2, (await service.ListAsync(Ct)).Count(r => r.Repository.Name == "app"));
    }

    private void Grant(DockerEnvironment environment, Role role) =>
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));

    private Stack AddStack(DockerEnvironment environment, RepositoryId repositoryId, string name = "web")
    {
        var stack = Stack.Create(
            environment.Id, StackName.Parse(name), repositoryId, BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        stacks.Add(stack);
        return stack;
    }
}
