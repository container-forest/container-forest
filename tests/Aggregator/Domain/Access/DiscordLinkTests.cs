using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

public class DiscordLinkTests
{
    [Fact]
    public void Code_is_short_readable_and_stored_only_as_a_hash()
    {
        var link = DiscordLink.For(UserId.New());

        var code = link.IssueCode(Now);

        Assert.Equal(DiscordLink.CodeLength, code.Length);
        Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I' or 'L');
        Assert.Equal(DiscordLink.HashCode(code), link.PendingCodeHash);
        Assert.NotEqual(code, link.PendingCodeHash);
        Assert.Equal(Now + DiscordLink.CodeLifetime, link.PendingCodeExpiresAt);
    }

    [Fact]
    public void Typed_code_ignores_case_spaces_and_dashes()
    {
        Assert.Equal(DiscordLink.HashCode("ABCD2345"), DiscordLink.HashCode("abcd-2345"));
        Assert.Equal(DiscordLink.HashCode("ABCD2345"), DiscordLink.HashCode(" abcd 2345 "));
    }

    [Fact]
    public void Completing_links_the_account_and_uses_up_the_code()
    {
        var link = DiscordLink.For(UserId.New());
        link.IssueCode(Now);

        link.CompleteLink(42, "alice\n", Now.AddMinutes(1));

        Assert.True(link.IsLinked);
        Assert.Equal(42UL, link.DiscordUserId);
        Assert.Equal("alice", link.DiscordUserName);
        Assert.Null(link.PendingCodeHash);
        Assert.Throws<DomainValidationException>(() => link.CompleteLink(43, "mallory", Now.AddMinutes(2)));
    }

    [Fact]
    public void Expired_code_does_not_link()
    {
        var link = DiscordLink.For(UserId.New());
        link.IssueCode(Now);

        Assert.Throws<DomainValidationException>(() => link.CompleteLink(42, "alice", Now + DiscordLink.CodeLifetime));
        Assert.False(link.IsLinked);
    }

    [Fact]
    public void Unlink_forgets_the_account()
    {
        var link = DiscordLink.For(UserId.New());
        link.IssueCode(Now);
        link.CompleteLink(42, "alice", Now);

        link.Unlink();

        Assert.False(link.IsLinked);
        Assert.Null(link.DiscordUserName);
    }

    [Fact]
    public void Discord_sign_in_links_without_a_code_and_keeps_a_pending_one()
    {
        var link = DiscordLink.For(UserId.New());
        link.IssueCode(TestData.Now);

        link.LinkVerified(42, " alice\n ", TestData.Now);

        Assert.True(link.IsLinked);
        Assert.Equal(42ul, link.DiscordUserId);
        Assert.Equal("alice", link.DiscordUserName);
        Assert.NotNull(link.PendingCodeHash);
    }

    [Fact]
    public void Discord_name_is_updated_only_while_linked()
    {
        var link = DiscordLink.For(UserId.New());

        link.UpdateDiscordUserName("ghost");
        Assert.Null(link.DiscordUserName);

        link.LinkVerified(42, "alice", TestData.Now);
        link.UpdateDiscordUserName("alice_new");
        Assert.Equal("alice_new", link.DiscordUserName);
    }
}
