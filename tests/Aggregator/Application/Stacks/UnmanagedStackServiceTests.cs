using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Stacks;

public class UnmanagedStackServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly FakeAuditTrail audit = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryRegistryCredentials credentials = new();
    private readonly FakeSecretProtector protector = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly UnmanagedStackService service;

    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: true, Now);

    public UnmanagedStackServiceTests()
    {
        service = new UnmanagedStackService(
            new CurrentActor(currentUser, users), environments, stacks, roleAssignments, credentials, protector, agent, audit);
        users.Add(alice);
        currentUser.Id = alice.Id;
        environments.Add(environment);
        agent.UnmanagedStacks.Add(new UnmanagedStackSnapshot("caddy", "/srv/docker/caddy", ["/srv/docker/caddy/compose.yaml"], false, true));
        agent.UnmanagedStacks.Add(new UnmanagedStackSnapshot("forest", "/srv/docker/forest", ["/srv/docker/forest/compose.yaml"], true, true));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_stacks_with_what_the_role_allows()
    {
        Grant(Role.Operator);

        var list = await service.ListAsync(environment.Id, Ct);

        Assert.Collection(
            list,
            v => Assert.Equal(("caddy", true, false), (v.Stack.Name, v.CanRestart, v.CanDeploy)),
            v => Assert.Equal(("forest", false, false), (v.Stack.Name, v.CanRestart, v.CanDeploy)));
    }

    [Fact]
    public async Task A_role_on_one_stack_only_shows_no_unmanaged_stacks_and_does_not_ask_the_agent()
    {
        var web = AddStack("web");
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, web, Role.Editor, Now));

        Assert.Empty(await service.ListAsync(environment.Id, Ct));
        Assert.Empty(agent.Calls);
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Up, null, Ct));
    }

    [Fact]
    public async Task An_invisible_environment_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(environment.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Restart, null, Ct));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task A_managed_stack_of_the_same_name_hides_the_unmanaged_one()
    {
        Grant(Role.Editor);
        AddStack("caddy");

        Assert.DoesNotContain(await service.ListAsync(environment.Id, Ct), v => v.Stack.Name == "caddy");
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Down, null, Ct));
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("unmanaged-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Operator_restarts_but_may_not_bring_up_or_take_down()
    {
        Grant(Role.Operator);
        agent.DeployOutput.Add("Container caddy Restarting");

        var result = await service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Restart, TimeSpan.FromSeconds(5), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["Container caddy Restarting"], result.Output);
        Assert.Contains("unmanaged-restart caddy", agent.Calls);
        Assert.Equal(TimeSpan.FromSeconds(5), agent.LastStopTimeout);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Up, null, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Down, null, Ct));
    }

    [Fact]
    public async Task Editor_pulls_with_all_of_the_environments_registry_logins()
    {
        Grant(Role.Editor);
        credentials.Add(RegistryCredential.Create(environment.Id, "ghcr.io", "bot", protector.Protect(SecretPurpose.RegistryPassword, "secret"), Now));
        credentials.Add(RegistryCredential.Create(EnvironmentId.New(), "gitea.example.com", "foreign", protector.Protect(SecretPurpose.RegistryPassword, "x"), Now));

        await service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Pull, null, Ct);

        Assert.Contains("unmanaged-pull caddy", agent.Calls);
        var login = Assert.Single(agent.LastRegistryLogins!);
        Assert.Equal(("ghcr.io", "bot", "secret"), (login.Server, login.Username, login.Password));
    }

    [Fact]
    public async Task Restart_and_down_get_no_registry_logins()
    {
        Grant(Role.Editor);
        credentials.Add(RegistryCredential.Create(environment.Id, "ghcr.io", "bot", protector.Protect(SecretPurpose.RegistryPassword, "secret"), Now));

        await service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Down, null, Ct);

        Assert.Empty(agent.LastRegistryLogins!);
    }

    [Fact]
    public async Task A_system_stack_refuses_everything()
    {
        Grant(Role.Editor);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ExecuteAsync(environment.Id, "forest", UnmanagedStackAction.Restart, null, Ct));
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("unmanaged-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_stack_is_not_found()
    {
        Grant(Role.Editor);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(environment.Id, "nothing", UnmanagedStackAction.Up, null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(environment.Id, "../etc", UnmanagedStackAction.Up, null, Ct));
    }

    [Fact]
    public async Task A_failed_compose_run_is_a_result_and_audited_as_failed()
    {
        Grant(Role.Editor);
        agent.DeployError = new AgentCommandFailedException(environment.Id, AgentErrorKind.CommandFailed, "docker compose up exited with code 1.");

        var result = await service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Up, null, Ct);

        Assert.False(result.Succeeded);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(
            (AuditAction.UnmanagedStackStarted, AuditOutcome.Failed, "caddy"),
            (entry.Action, entry.Outcome, entry.Target?.Name));
    }

    [Fact]
    public async Task A_refused_attempt_is_audited()
    {
        Grant(Role.Viewer);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(environment.Id, "caddy", UnmanagedStackAction.Down, null, Ct));

        Assert.Equal(AuditOutcome.Denied, Assert.Single(audit.Entries).Outcome);
    }

    private void Grant(Role role) => roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, environment.Id, role, Now));

    private Stack AddStack(string name)
    {
        var created = Stack.Create(
            environment.Id,
            StackName.Parse(name),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem: false,
            Now);
        stacks.Add(created);
        return created;
    }
}
