using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Environments;

public class EnvironmentAdministrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly FakeAgentChannel agent = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly EnvironmentService service;
    private readonly User root = User.CreateAdministrator("root", Now);

    public EnvironmentAdministrationTests()
    {
        users.Add(root);
        service = ServiceFor(root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // CurrentActor remembers its user for the request, as in the aggregator, so each user gets its own service.
    private EnvironmentService ServiceFor(User user) => new(
        new CurrentActor(new FakeCurrentUser { Id = user.Id }, users), environments, roleAssignments, stacks, unitOfWork, agent, new Deployments.FixedTime(Now));

    [Fact]
    public async Task Environment_is_renamed_and_made_private()
    {
        var created = await service.CreateAsync("staging", isPrivate: false, Ct);

        var updated = await service.UpdateAsync(created.Id, " prod ", isPrivate: true, Ct);

        Assert.Equal(("prod", true), (updated.Name, updated.IsPrivate));
    }

    [Fact]
    public async Task Name_of_another_environment_is_a_conflict_but_a_new_case_is_not()
    {
        await service.CreateAsync("prod", isPrivate: false, Ct);
        var staging = await service.CreateAsync("staging", isPrivate: false, Ct);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.UpdateAsync(staging.Id, "PROD", isPrivate: false, Ct));
        Assert.Equal("Staging", (await service.UpdateAsync(staging.Id, "Staging", isPrivate: false, Ct)).Name);
    }

    [Fact]
    public async Task Pending_enrollment_token_is_shown_to_administrators_only()
    {
        var created = await service.CreateAsync("prod", isPrivate: false, Ct);
        Assert.Equal(created.EnrollmentToken.ExpiresAt, Assert.Single(await service.ListVisibleAsync(Ct)).EnrollmentTokenExpiresAt);

        var alice = User.Create("alice", Now);
        users.Add(alice);
        Assert.Null(Assert.Single(await ServiceFor(alice).ListVisibleAsync(Ct)).EnrollmentTokenExpiresAt);
    }

    [Fact]
    public async Task Revoking_forgets_the_agent_and_drops_its_connection()
    {
        var created = await service.CreateAsync("prod", isPrivate: false, Ct);
        var environment = await environments.GetAsync(created.Id, Ct);
        Assert.True(environment.TryEnroll(created.EnrollmentToken.Token, "host", Now, out _));

        await service.RevokeAgentAsync(created.Id, Ct);

        Assert.False(environment.IsEnrolled);
        Assert.Equal([created.Id], agent.Disconnected);
    }

    [Fact]
    public async Task Only_administrators_change_environments()
    {
        var created = await service.CreateAsync("prod", isPrivate: false, Ct);
        var alice = User.Create("alice", Now);
        users.Add(alice);
        var asAlice = ServiceFor(alice);

        await Assert.ThrowsAsync<AccessDeniedException>(() => asAlice.UpdateAsync(created.Id, "x", isPrivate: false, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => asAlice.RevokeAgentAsync(created.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => asAlice.RemoveAsync(created.Id, Ct));
        Assert.Empty(agent.Disconnected);
        Assert.NotNull(await environments.FindAsync(created.Id, Ct));
    }

    [Fact]
    public async Task Environment_without_stacks_is_removed_and_its_agent_dropped()
    {
        var created = await service.CreateAsync("prod", isPrivate: false, Ct);

        await service.RemoveAsync(created.Id, Ct);

        Assert.Null(await environments.FindAsync(created.Id, Ct));
        Assert.Equal([created.Id], agent.Disconnected);
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveAsync(created.Id, Ct));
    }

    [Fact]
    public async Task Environment_with_stacks_is_not_removed()
    {
        var created = await service.CreateAsync("prod", isPrivate: false, Ct);
        stacks.Add(Stack.Create(
            created.Id, StackName.Parse("web"), RepositoryId.New(), BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"),
            isSystem: false, Now));

        await Assert.ThrowsAsync<DomainConflictException>(() => service.RemoveAsync(created.Id, Ct));
        Assert.NotNull(await environments.FindAsync(created.Id, Ct));
        Assert.Empty(agent.Disconnected);
    }
}
