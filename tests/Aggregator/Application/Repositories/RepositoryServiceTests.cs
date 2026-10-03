using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Repositories;

public class RepositoryServiceTests
{
    private readonly InMemoryUsers users = new();
    private readonly InMemoryGitRepositories repositories = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeSecretProtector protector = new();
    private readonly RepositoryService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly GitRepository infra = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);

    public RepositoryServiceTests()
    {
        service = new RepositoryService(
            new CurrentActor(currentUser, users), repositories, new InMemoryRoleAssignments(), protector, unitOfWork, new FixedTime(Now));
        users.Add(admin);
        currentUser.Id = admin.Id;
        repositories.Add(infra);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_a_private_repository_with_an_encrypted_token()
    {
        var repository = await service.CreateAsync(
            "apps", "https://git.example.com/team/apps.git", new RepositoryAccessToken("deploy", "glpat-secret"), Ct);

        Assert.Equal("deploy", repository.AccessUserName);
        Assert.Equal("glpat-secret", protector.Unprotect(SecretPurpose.GitAccessToken, repository.ProtectedAccessToken!));
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ghp_ secret")]
    [InlineData("ghp_secret\n")]
    public async Task Rejects_a_token_with_whitespace_or_empty(string token)
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken(null, token), Ct));

        Assert.False(infra.HasAccessToken);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Sets_replaces_and_removes_the_token()
    {
        await service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken(null, "first"), Ct);
        await service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken("bot", "second"), Ct);

        Assert.Equal("second", protector.Unprotect(SecretPurpose.GitAccessToken, infra.ProtectedAccessToken!));
        Assert.Equal("bot", infra.AccessUserName);

        await service.RemoveAccessTokenAsync(infra.Id, Ct);

        Assert.False(infra.HasAccessToken);
        Assert.Equal(3, unitOfWork.Saves);
    }

    [Fact]
    public async Task Renames_and_moves_within_the_host_keeping_the_token()
    {
        await service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken(null, "token"), Ct);

        var updated = await service.UpdateAsync(infra.Id, "Infra", "https://git.example.com/ops/infra.git", Ct);

        Assert.Equal(("Infra", "/ops/infra.git", true), (updated.Name, updated.Url.AbsolutePath, updated.HasAccessToken));
    }

    [Fact]
    public async Task Token_does_not_follow_the_repository_to_another_host()
    {
        await service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken(null, "token"), Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAsync(infra.Id, "infra", "https://elsewhere.example.com/team/infra.git", Ct));

        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Name_of_another_repository_is_taken()
    {
        repositories.Add(GitRepository.Create("apps", "https://git.example.com/team/apps.git", Now));

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.UpdateAsync(infra.Id, "APPS", infra.Url.ToString(), Ct));
    }

    [Fact]
    public async Task Only_administrators_manage_repositories()
    {
        var alice = User.Create("alice", Now);
        users.Add(alice);
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.CreateAsync("apps", "https://git.example.com/team/apps.git", null, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.UpdateAsync(infra.Id, "infra", infra.Url.ToString(), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.SetAccessTokenAsync(infra.Id, new RepositoryAccessToken(null, "token"), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RemoveAccessTokenAsync(infra.Id, Ct));
    }

    [Fact]
    public async Task Unknown_repository_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveAccessTokenAsync(RepositoryId.New(), Ct));

    [Fact]
    public void Access_token_does_not_print_the_token() =>
        Assert.DoesNotContain("ghp_secret", new RepositoryAccessToken("deploy", "ghp_secret").ToString(), StringComparison.Ordinal);
}
