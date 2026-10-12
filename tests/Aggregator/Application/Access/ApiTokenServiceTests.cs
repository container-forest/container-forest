using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Tests.Access;

public class ApiTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryApiTokens tokens = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();

    private readonly User alice = User.Create("alice", Now);
    private readonly User root = User.CreateAdministrator("root", Now);
    private readonly DockerEnvironment publicEnvironment = DockerEnvironment.Create("public", isPrivate: false, Now);
    private readonly DockerEnvironment privateEnvironment = DockerEnvironment.Create("private", isPrivate: true, Now);

    public ApiTokenServiceTests()
    {
        users.Add(alice);
        users.Add(root);
        environments.Add(publicEnvironment);
        environments.Add(privateEnvironment);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Created_token_authenticates_as_its_user()
    {
        var created = await ServiceFor(alice).CreateAsync("laptop", readOnly: false, null, null, Ct);

        var found = await Authenticator(Now).AuthenticateAsync(created.Secret, Ct);

        Assert.NotNull(found);
        Assert.Equal(alice.Id, found.UserId);
        Assert.Equal(Now, found.LastUsedAt);
    }

    [Fact]
    public async Task Unknown_malformed_and_expired_tokens_do_not_authenticate()
    {
        var created = await ServiceFor(alice).CreateAsync("ci", readOnly: false, null, Now.AddDays(1), Ct);

        Assert.Null(await Authenticator(Now).AuthenticateAsync(ApiToken.Prefix + "nope", Ct));
        Assert.Null(await Authenticator(Now).AuthenticateAsync(created.Secret[ApiToken.Prefix.Length..], Ct));
        Assert.Null(await Authenticator(Now.AddDays(1)).AuthenticateAsync(created.Secret, Ct));
    }

    [Fact]
    public async Task Revoked_token_no_longer_authenticates()
    {
        var created = await ServiceFor(alice).CreateAsync("laptop", false, null, null, Ct);

        await ServiceFor(alice).RevokeMineAsync(created.Token.Id, Ct);

        Assert.Null(await Authenticator(Now).AuthenticateAsync(created.Secret, Ct));
    }

    [Fact]
    public async Task Token_cannot_manage_tokens()
    {
        var created = await ServiceFor(alice).CreateAsync("laptop", false, null, null, Ct);
        currentUser.UseToken(created.Token);
        var service = Service();

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateAsync("wider", false, null, null, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListMineAsync(Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RevokeMineAsync(created.Token.Id, Ct));
    }

    [Fact]
    public async Task Token_can_revoke_itself()
    {
        var created = await ServiceFor(alice).CreateAsync("laptop", readOnly: true, null, null, Ct);
        currentUser.UseToken(created.Token);

        await Service().RevokeCurrentAsync(Ct);

        Assert.Empty(await ServiceFor(alice).ListMineAsync(Ct));
    }

    [Fact]
    public async Task Session_has_no_current_token_to_revoke() =>
        await Assert.ThrowsAsync<DomainValidationException>(() => ServiceFor(alice).RevokeCurrentAsync(Ct));

    [Fact]
    public async Task Token_may_be_limited_to_visible_environments_only()
    {
        var service = ServiceFor(alice);

        var created = await service.CreateAsync("ci", false, [publicEnvironment.Id], null, Ct);
        Assert.Equal([publicEnvironment.Id], created.Token.Environments);

        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync("ci", false, [privateEnvironment.Id], null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync("ci", false, [EnvironmentId.New()], null, Ct));
    }

    [Fact]
    public async Task Users_cannot_revoke_each_others_tokens()
    {
        var created = await ServiceFor(root).CreateAsync("root", false, null, null, Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => ServiceFor(alice).RevokeMineAsync(created.Token.Id, Ct));
    }

    [Fact]
    public async Task Administrator_lists_and_revokes_anyones_tokens()
    {
        var created = await ServiceFor(alice).CreateAsync("laptop", false, null, null, Ct);

        var listed = await ServiceFor(root).ListForUserAsync(alice.Id, Ct);
        Assert.Equal(created.Token.Id, Assert.Single(listed).Id);

        await ServiceFor(root).RevokeForUserAsync(alice.Id, created.Token.Id, Ct);
        Assert.Empty(await ServiceFor(alice).ListMineAsync(Ct));
    }

    [Fact]
    public async Task Regular_users_cannot_see_others_tokens() =>
        await Assert.ThrowsAsync<AccessDeniedException>(() => ServiceFor(alice).ListForUserAsync(root.Id, Ct));

    [Fact]
    public async Task Number_of_tokens_is_limited()
    {
        var service = ServiceFor(alice);
        for (var i = 0; i < ApiTokenService.MaxTokensPerUser; i++)
        {
            await service.CreateAsync($"t{i}", false, null, null, Ct);
        }

        await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateAsync("one more", false, null, null, Ct));
    }

    // A new service per acting user: CurrentActor remembers the user of its scope.
    private ApiTokenService ServiceFor(User user)
    {
        currentUser.Id = user.Id;
        currentUser.Scope = AccessScope.Full;
        currentUser.ApiTokenId = null;
        return Service();
    }

    private ApiTokenService Service() => new(
        new CurrentActor(currentUser, users), tokens, users, roleAssignments, environments, unitOfWork, new FakeAuditTrail(), new FixedTime(Now));

    private ApiTokenAuthenticator Authenticator(DateTimeOffset now) => new(tokens, unitOfWork, new FixedTime(now));
}
