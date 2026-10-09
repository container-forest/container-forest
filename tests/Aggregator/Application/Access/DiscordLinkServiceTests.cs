using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Tests.Access;

public class DiscordLinkServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryDiscordLinks links = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly MovableTime time = new(Now);

    private readonly User alice = User.Create("alice", Now);
    private readonly User bob = User.Create("bob", Now);

    public DiscordLinkServiceTests()
    {
        users.Add(alice);
        users.Add(bob);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Code_from_the_panel_links_the_Discord_account()
    {
        var code = await ServiceFor(alice).IssueCodeAsync(Ct);

        var linked = await ServiceFor(null).LinkAsync(42, "alice#1", code.Code, Ct);

        Assert.Same(alice, linked);
        Assert.Same(alice, await ServiceFor(null).FindLinkedUserAsync(42, Ct));
        var status = await ServiceFor(alice).GetStatusAsync(Ct);
        Assert.True(status.Linked);
        Assert.Equal("alice#1", status.DiscordUserName);
    }

    [Fact]
    public async Task New_code_replaces_the_previous_one()
    {
        var first = await ServiceFor(alice).IssueCodeAsync(Ct);
        await ServiceFor(alice).IssueCodeAsync(Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() => ServiceFor(null).LinkAsync(42, "alice", first.Code, Ct));
    }

    [Fact]
    public async Task Expired_and_unknown_codes_get_the_same_answer()
    {
        var code = await ServiceFor(alice).IssueCodeAsync(Ct);
        time.Now += DiscordLink.CodeLifetime;

        var expired = await Assert.ThrowsAsync<DomainValidationException>(() => ServiceFor(null).LinkAsync(42, "a", code.Code, Ct));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => ServiceFor(null).LinkAsync(42, "a", "ZZZZZZZZ", Ct));
        Assert.Equal(unknown.Message, expired.Message);
    }

    [Fact]
    public async Task Linking_the_same_Discord_account_again_moves_the_link()
    {
        await LinkAsync(alice, 42);

        await LinkAsync(bob, 42);

        Assert.Same(bob, await ServiceFor(null).FindLinkedUserAsync(42, Ct));
        Assert.False((await ServiceFor(alice).GetStatusAsync(Ct)).Linked);
    }

    [Fact]
    public async Task Deactivated_user_cannot_complete_a_link()
    {
        var code = await ServiceFor(alice).IssueCodeAsync(Ct);
        alice.Deactivate();

        await Assert.ThrowsAsync<AccessDeniedException>(() => ServiceFor(null).LinkAsync(42, "alice", code.Code, Ct));
    }

    [Fact]
    public async Task Unlinked_account_has_no_user()
    {
        await LinkAsync(alice, 42);

        await ServiceFor(alice).UnlinkAsync(Ct);

        Assert.Null(await ServiceFor(null).FindLinkedUserAsync(42, Ct));
    }

    private async Task LinkAsync(User user, ulong discordUserId)
    {
        var code = await ServiceFor(user).IssueCodeAsync(Ct);
        await ServiceFor(null).LinkAsync(discordUserId, user.UserName, code.Code, Ct);
    }

    [Fact]
    public async Task Discord_sign_in_link_moves_the_account_like_a_code()
    {
        await ServiceFor(null).LinkVerifiedAsync(alice.Id, 42, "shared", Ct);

        await ServiceFor(null).LinkVerifiedAsync(bob.Id, 42, "shared", Ct);

        Assert.Same(bob, await ServiceFor(null).FindLinkedUserAsync(42, Ct));
        Assert.False((await ServiceFor(alice).GetStatusAsync(Ct)).Linked);
    }

    [Fact]
    public async Task Deactivated_user_cannot_link_through_discord()
    {
        bob.Deactivate();

        await Assert.ThrowsAsync<AccessDeniedException>(() => ServiceFor(null).LinkVerifiedAsync(bob.Id, 42, "bob", Ct));
    }

    [Fact]
    public async Task Discord_sign_in_finds_only_an_active_linked_user_and_refreshes_the_name()
    {
        await ServiceFor(null).LinkVerifiedAsync(alice.Id, 42, "alice", Ct);

        Assert.Same(alice, await ServiceFor(null).FindUserForSignInAsync(42, "alice_renamed", Ct));
        Assert.Equal("alice_renamed", (await ServiceFor(alice).GetStatusAsync(Ct)).DiscordUserName);
        Assert.Null(await ServiceFor(null).FindUserForSignInAsync(7, "stranger", Ct));

        alice.Deactivate();
        Assert.Null(await ServiceFor(null).FindUserForSignInAsync(42, "alice", Ct));
    }

    // A new service per acting user: CurrentActor remembers the user of its scope.
    private DiscordLinkService ServiceFor(User? user)
    {
        currentUser.Id = user?.Id;
        return new DiscordLinkService(new CurrentActor(currentUser, users), links, users, new FakeUnitOfWork(), time);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
