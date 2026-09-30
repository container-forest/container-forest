using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Registries;

public class RegistryCredentialServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryRegistryCredentials credentials = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly RegistryCredentialService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment environment = DockerEnvironment.Create("prod", isPrivate: false, Now);

    public RegistryCredentialServiceTests()
    {
        users.Add(admin);
        users.Add(alice);
        environments.Add(environment);
        currentUser.Id = admin.Id;
        service = new RegistryCredentialService(
            new CurrentActor(currentUser, users), environments, credentials, new FakeSecretProtector(), unitOfWork, new FixedTime(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Set_stores_the_password_protected_and_never_returns_it()
    {
        var (summary, created) = await service.SetAsync(environment.Id, "https://ghcr.io", "bot", "token-1", Ct);

        Assert.True(created);
        Assert.Equal("ghcr.io", summary.Server);
        Assert.Equal("bot", summary.Username);
        Assert.DoesNotContain("token-1", summary.ToString());
        var stored = Assert.Single(credentials.All);
        Assert.Equal("RegistryPassword:token-1", stored.ProtectedPassword);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Setting_the_same_registry_again_replaces_the_login()
    {
        var (first, _) = await service.SetAsync(environment.Id, "ghcr.io", "bot", "token-1", Ct);

        var (second, created) = await service.SetAsync(environment.Id, "GHCR.io", "ci", "token-2", Ct);

        Assert.False(created);
        Assert.Equal(first.Id, second.Id);
        var stored = Assert.Single(credentials.All);
        Assert.Equal("ci", stored.Username);
        Assert.Equal("RegistryPassword:token-2", stored.ProtectedPassword);
    }

    [Fact]
    public async Task Each_environment_has_its_own_credentials()
    {
        var other = DockerEnvironment.Create("staging", isPrivate: false, Now);
        environments.Add(other);

        await service.SetAsync(environment.Id, "ghcr.io", "prod-bot", "p", Ct);
        await service.SetAsync(other.Id, "ghcr.io", "staging-bot", "s", Ct);

        Assert.Equal(["prod-bot"], (await service.ListAsync(environment.Id, Ct)).Select(c => c.Username));
        Assert.Equal(["staging-bot"], (await service.ListAsync(other.Id, Ct)).Select(c => c.Username));
    }

    [Fact]
    public async Task Only_administrators_manage_credentials()
    {
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetAsync(environment.Id, "ghcr.io", "bot", "p", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(environment.Id, Ct));
        Assert.Empty(credentials.All);
    }

    [Fact]
    public async Task Unknown_environment_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.SetAsync(EnvironmentId.New(), "ghcr.io", "bot", "p", Ct));
    }

    [Fact]
    public async Task Empty_password_is_rejected()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SetAsync(environment.Id, "ghcr.io", "bot", "", Ct));
    }

    [Fact]
    public async Task Delete_removes_the_credential_only_in_its_environment()
    {
        var (summary, _) = await service.SetAsync(environment.Id, "ghcr.io", "bot", "p", Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(EnvironmentId.New(), summary.Id, Ct));
        Assert.Single(credentials.All);

        await service.DeleteAsync(environment.Id, summary.Id, Ct);
        Assert.Empty(credentials.All);
    }
}
