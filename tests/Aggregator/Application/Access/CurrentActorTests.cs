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

        Assert.Same(alice, await actor.GetAsync(Ct));
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

        Assert.Same(admin, await actor.GetAdministratorAsync(Ct));
    }

    private User SignIn(User user)
    {
        users.Add(user);
        currentUser.Id = user.Id;
        return user;
    }
}
