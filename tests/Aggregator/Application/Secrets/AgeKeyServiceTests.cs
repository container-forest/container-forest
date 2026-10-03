using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Secrets;

public class AgeKeyServiceTests
{
    private const string Recipient = "age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng";

    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryAgeKeys keys = new();
    private readonly FakeAgeKeyGenerator generator = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly AgeKeyService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment publicEnvironment = DockerEnvironment.Create("staging", isPrivate: false, Now);
    private readonly DockerEnvironment privateEnvironment = DockerEnvironment.Create("prod", isPrivate: true, Now);

    public AgeKeyServiceTests()
    {
        users.Add(admin);
        users.Add(alice);
        environments.Add(publicEnvironment);
        environments.Add(privateEnvironment);
        currentUser.Id = admin.Id;
        service = new AgeKeyService(
            new CurrentActor(currentUser, users),
            environments,
            roleAssignments,
            keys,
            generator,
            new FakeSecretProtector(),
            unitOfWork,
            new FixedTime(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Administrator_creates_a_key_whose_private_part_is_stored_protected()
    {
        generator.Keys.Enqueue(new GeneratedAgeKey(Recipient, "AGE-SECRET-KEY-1SECRET"));

        var summary = await service.CreateAsync(privateEnvironment.Id, Ct);

        Assert.Equal(Recipient, summary.Recipient);
        Assert.DoesNotContain("SECRET", summary.ToString(), StringComparison.Ordinal);
        var stored = Assert.Single(keys.All);
        Assert.Equal((privateEnvironment.Id, "AgeIdentity:AGE-SECRET-KEY-1SECRET"), (stored.EnvironmentId, stored.ProtectedIdentity));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Whoever_sees_the_environment_reads_its_public_keys()
    {
        keys.Add(AgeKey.Create(publicEnvironment.Id, Recipient, "protected", Now));
        currentUser.Id = alice.Id;

        var listed = await service.ListAsync(publicEnvironment.Id, Ct);

        Assert.Equal(Recipient, Assert.Single(listed).Recipient);
    }

    [Fact]
    public async Task Invisible_environment_is_not_found()
    {
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(privateEnvironment.Id, Ct));
    }

    [Fact]
    public async Task Only_administrators_create_and_delete_keys()
    {
        var key = AgeKey.Create(publicEnvironment.Id, Recipient, "protected", Now);
        keys.Add(key);
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, publicEnvironment.Id, Role.Editor, Now));
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateAsync(publicEnvironment.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(publicEnvironment.Id, key.Id, Ct));
        Assert.Single(keys.All);
    }

    [Fact]
    public async Task Key_is_deleted_only_through_its_own_environment()
    {
        var key = AgeKey.Create(publicEnvironment.Id, Recipient, "protected", Now);
        keys.Add(key);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(privateEnvironment.Id, key.Id, Ct));
        await service.DeleteAsync(publicEnvironment.Id, key.Id, Ct);

        Assert.Empty(keys.All);
    }
}
