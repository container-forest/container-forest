using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Repositories;

public class GitConnectionServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryGitRepositories repositories = new();
    private readonly InMemoryGitConnections connections = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeSecretProtector protector = new();
    private readonly GitConnectionService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment production = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly DockerEnvironment staging = DockerEnvironment.Create("staging", isPrivate: true, Now);

    public GitConnectionServiceTests()
    {
        var actor = new CurrentActor(currentUser, users);
        service = new GitConnectionService(
            actor,
            new RepositoryAccess(actor, roleAssignments, new InMemoryStacks(), environments),
            connections,
            repositories,
            environments,
            protector,
            unitOfWork,
            new FakeAuditTrail(),
            new FixedTime(Now));
        users.Add(admin);
        users.Add(alice);
        environments.Add(production);
        environments.Add(staging);
        currentUser.Id = admin.Id;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_a_connection_with_an_encrypted_token_and_its_environments()
    {
        var created = await service.CreateAsync(
            "team",
            "https://gitea.example.com/team/",
            new GitAccessToken("forest-team", "gitea-token"),
            new ConnectionEnvironments(false, [production.Id]),
            Ct);

        var connection = created.Connection;
        Assert.Equal("forest-team", connection.AccessUserName);
        Assert.Equal("gitea-token", protector.Unprotect(SecretPurpose.GitAccessToken, connection.ProtectedAccessToken!));
        Assert.Equal([production.Id], connection.AllowedEnvironments);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ghp_ secret")]
    [InlineData("ghp_secret\n")]
    public async Task Rejects_a_token_with_whitespace_or_empty(string token)
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(
            "team", "https://gitea.example.com/team/", new GitAccessToken(null, token), new ConnectionEnvironments(true, []), Ct));

        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Unknown_environment_is_rejected() =>
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(
            "team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(false, [EnvironmentId.New()]), Ct));

    [Fact]
    public async Task Names_are_unique()
    {
        await service.CreateAsync("team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(true, []), Ct);

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.CreateAsync("TEAM", "https://gitea.example.com/other/", null, new ConnectionEnvironments(true, []), Ct));
    }

    [Fact]
    public async Task Sets_replaces_and_removes_the_token()
    {
        var id = (await service.CreateAsync("team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(true, []), Ct)).Connection.Id;

        await service.SetAccessTokenAsync(id, new GitAccessToken(null, "first"), Ct);
        var replaced = await service.SetAccessTokenAsync(id, new GitAccessToken("bot", "second"), Ct);
        Assert.Equal("second", protector.Unprotect(SecretPurpose.GitAccessToken, replaced.Connection.ProtectedAccessToken!));

        var removed = await service.RemoveAccessTokenAsync(id, Ct);
        Assert.False(removed.Connection.HasAccessToken);
    }

    [Fact]
    public async Task Prefix_change_keeps_repositories_inside_and_counts_them()
    {
        var connection = (await service.CreateAsync(
            "team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(true, []), Ct)).Connection;
        repositories.Add(GitRepository.Create("app", "https://gitea.example.com/team/app.git", connection, Now));

        await Assert.ThrowsAsync<DomainConflictException>(() => service.UpdateAsync(
            connection.Id, "team", "https://gitea.example.com/billing/", new ConnectionEnvironments(true, []), Ct));

        var updated = await service.UpdateAsync(
            connection.Id, "gitea", "https://gitea.example.com/", new ConnectionEnvironments(false, [staging.Id]), Ct);
        Assert.Equal(("gitea", 1, false), (updated.Connection.Name, updated.RepositoryCount, updated.Connection.AllowsAllEnvironments));
    }

    [Fact]
    public async Task Connection_in_use_is_not_deleted()
    {
        var connection = (await service.CreateAsync(
            "team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(true, []), Ct)).Connection;
        var app = GitRepository.Create("app", "https://gitea.example.com/team/app.git", connection, Now);
        repositories.Add(app);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.DeleteAsync(connection.Id, Ct));

        repositories.Remove(app);
        await service.DeleteAsync(connection.Id, Ct);
        Assert.Null(await connections.FindAsync(connection.Id, Ct));
    }

    [Fact]
    public async Task Webhook_secret_is_shown_once_and_stored_encrypted()
    {
        var id = (await service.CreateAsync("team", "https://gitea.example.com/team/", null, new ConnectionEnvironments(true, []), Ct)).Connection.Id;

        var (connection, secret) = await service.CreateWebhookSecretAsync(id, Ct);

        Assert.Equal(secret, protector.Unprotect(SecretPurpose.WebhookSecret, connection.Connection.ProtectedWebhookSecret!));
        Assert.False((await service.RemoveWebhookSecretAsync(id, Ct)).Connection.HasWebhook);
    }

    [Fact]
    public async Task Only_administrators_change_connections()
    {
        var id = AddConnection("team", "https://gitea.example.com/team/", true).Id;
        currentUser.Id = alice.Id;
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, production.Id, Role.Editor, Now));

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.CreateAsync("mine", "https://gitea.example.com/", null, new ConnectionEnvironments(true, []), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.UpdateAsync(id, "team", "https://gitea.example.com/", new ConnectionEnvironments(true, []), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetAccessTokenAsync(id, new GitAccessToken(null, "t"), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateWebhookSecretAsync(id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(id, Ct));
    }

    [Fact]
    public async Task Editor_lists_connections_of_their_environments_and_sees_only_those_environments()
    {
        AddConnection("team", "https://gitea.example.com/team/", false, production.Id, staging.Id);
        AddConnection("billing", "https://gitea.example.com/billing/", false, staging.Id);
        currentUser.Id = alice.Id;
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, production.Id, Role.Editor, Now));

        var listed = Assert.Single(await service.ListAsync(Ct));

        Assert.Equal("team", listed.Connection.Name);
        Assert.Equal([production.Id], listed.EnvironmentIds);
    }

    // Straight into the repository: CurrentActor keeps the first user it loads, as it does for one request.
    private GitConnection AddConnection(string name, string prefix, bool all, params EnvironmentId[] environmentIds)
    {
        var connection = GitConnection.Create(name, prefix, Now);
        connection.AllowEnvironments(all, environmentIds);
        connections.Add(connection);
        return connection;
    }

    [Fact]
    public void Access_token_does_not_print_the_token() =>
        Assert.DoesNotContain("ghp_secret", new GitAccessToken("deploy", "ghp_secret").ToString(), StringComparison.Ordinal);
}
