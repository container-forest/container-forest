using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Tests.Access;

public class CurrentActorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly CurrentActor actor;

    public CurrentActorTests() => actor = new CurrentActor(currentUser, users);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_the_signed_in_user()
    {
        var alice = SignIn(User.Create("alice", Now));

        var current = await actor.GetAsync(Ct);

        Assert.Same(alice, current.User);
        Assert.Same(AccessScope.Full, current.Scope);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthenticated() =>
        await Assert.ThrowsAsync<UnauthenticatedException>(() => actor.GetAsync(Ct));

    [Fact]
    public async Task Session_of_a_deleted_user_is_unauthenticated()
    {
        currentUser.Id = UserId.New();

        await Assert.ThrowsAsync<UnauthenticatedException>(() => actor.GetAsync(Ct));
    }

    [Fact]
    public async Task Deactivated_user_is_denied()
    {
        var alice = SignIn(User.Create("alice", Now));
        alice.Deactivate();

        await Assert.ThrowsAsync<AccessDeniedException>(() => actor.GetAsync(Ct));
    }

    [Fact]
    public async Task Administrator_check_denies_regular_users()
    {
        SignIn(User.Create("alice", Now));

        await Assert.ThrowsAsync<AccessDeniedException>(() => actor.GetAdministratorAsync(Ct));
    }

    [Fact]
    public async Task Administrator_check_passes_administrators()
    {
        var admin = SignIn(User.CreateAdministrator("root", Now));

        Assert.Same(admin, (await actor.GetAdministratorAsync(Ct)).User);
    }

    [Fact]
    public async Task Token_carries_its_limits()
    {
        var alice = SignIn(User.Create("alice", Now));
        var environmentId = EnvironmentId.New();
        var (token, _) = ApiToken.Create(alice.Id, "ci", readOnly: true, [environmentId], expiresAt: null, Now);
        currentUser.UseToken(token);

        var current = await actor.GetAsync(Ct);

        Assert.True(current.Scope.IsReadOnly);
        Assert.True(current.Scope.Reaches(environmentId));
        Assert.False(current.Scope.Reaches(EnvironmentId.New()));
        Assert.Equal(token.Id, actor.ApiTokenId);
    }

    [Fact]
    public async Task Administrator_check_denies_an_administrators_restricted_token()
    {
        var admin = SignIn(User.CreateAdministrator("root", Now));
        currentUser.UseToken(ApiToken.Create(admin.Id, "ci", readOnly: false, [EnvironmentId.New()], null, Now).Token);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => actor.GetAdministratorAsync(Ct));
        Assert.Contains("limited to some environments", denied.Message);
    }

    [Fact]
    public async Task Administrator_check_passes_an_administrators_unrestricted_token()
    {
        var admin = SignIn(User.CreateAdministrator("root", Now));
        currentUser.UseToken(ApiToken.Create(admin.Id, "cli", readOnly: false, null, null, Now).Token);

        Assert.Same(admin, (await actor.GetAdministratorAsync(Ct)).User);
    }

    [Fact]
    public async Task Signed_in_user_is_the_sessions_user()
    {
        var alice = SignIn(User.Create("alice", Now));

        Assert.Same(alice, await actor.GetSignedInUserAsync(Ct));
    }

    [Fact]
    public async Task Signed_in_user_denies_a_token()
    {
        var alice = SignIn(User.Create("alice", Now));
        currentUser.UseToken(ApiToken.Create(alice.Id, "cli", readOnly: false, null, null, Now).Token);

        await Assert.ThrowsAsync<AccessDeniedException>(() => actor.GetSignedInUserAsync(Ct));
    }

    private User SignIn(User user)
    {
        users.Add(user);
        currentUser.Id = user.Id;
        return user;
    }
}
