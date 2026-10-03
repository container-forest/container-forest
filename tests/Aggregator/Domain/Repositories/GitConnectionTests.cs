using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Repositories;

public class GitConnectionTests
{
    private static readonly EnvironmentId Production = EnvironmentId.New();
    private static readonly EnvironmentId Staging = EnvironmentId.New();

    [Fact]
    public void A_new_connection_is_allowed_nowhere()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);

        Assert.False(connection.IsAllowedIn(Production));
        Assert.False(connection.AllowsAllEnvironments);
        Assert.Empty(connection.AllowedEnvironments);
    }

    [Fact]
    public void Allowed_environments_are_replaced_as_a_whole()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);

        connection.AllowEnvironments(false, [Production, Staging, Production]);
        Assert.Equal(2, connection.AllowedEnvironments.Count);

        connection.AllowEnvironments(false, [Staging]);
        Assert.False(connection.IsAllowedIn(Production));
        Assert.True(connection.IsAllowedIn(Staging));
    }

    [Fact]
    public void All_environments_include_future_ones_and_forget_the_list()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);
        connection.AllowEnvironments(false, [Production]);

        connection.AllowEnvironments(true, [Production]);

        Assert.True(connection.IsAllowedIn(EnvironmentId.New()));
        Assert.Empty(connection.AllowedEnvironments);
    }

    [Fact]
    public void The_prefix_cannot_leave_repositories_outside()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);
        Uri[] repositories = [new("https://gitea.example.com/team/app.git")];

        Assert.Throws<DomainConflictException>(() => connection.ChangeUrlPrefix("https://gitea.example.com/team/other/", repositories));
        Assert.Equal("https://gitea.example.com/team/", connection.UrlPrefix.ToString());

        connection.ChangeUrlPrefix("https://gitea.example.com/", repositories);
        Assert.Equal("https://gitea.example.com/", connection.UrlPrefix.ToString());
    }

    [Fact]
    public void A_token_stays_on_its_host()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);
        connection.SetAccessToken(null, "protected");

        Assert.Throws<DomainValidationException>(() => connection.ChangeUrlPrefix("https://github.com/team/", []));

        connection.RemoveAccessToken();
        connection.ChangeUrlPrefix("https://github.com/team/", []);
        Assert.Equal("github.com", connection.UrlPrefix.Host);
    }

    [Fact]
    public void Access_token_and_user_name_are_set_and_removed()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);

        connection.SetAccessToken("  forest-team  ", "protected");
        Assert.True(connection.HasAccessToken);
        Assert.Equal("forest-team", connection.AccessUserName);

        connection.SetAccessToken(" ", "protected");
        Assert.Null(connection.AccessUserName);

        connection.RemoveAccessToken();
        Assert.False(connection.HasAccessToken);
        Assert.Throws<DomainValidationException>(() => connection.SetAccessToken("a:b", "protected"));
        Assert.Throws<DomainValidationException>(() => connection.SetAccessToken(null, " "));
    }

    [Fact]
    public void Webhook_secret_is_set_and_removed()
    {
        var connection = GitConnection.Create("team", "https://gitea.example.com/team/", Now);

        connection.SetWebhookSecret("protected");
        Assert.True(connection.HasWebhook);

        connection.RemoveWebhookSecret();
        Assert.False(connection.HasWebhook);
    }

    [Theory]
    [InlineData("http://gitea.example.com/team/")]
    [InlineData("https://user:pass@gitea.example.com/")]
    [InlineData("https://gitea.example.com/team%2F/")]
    public void Rejects_unsupported_prefixes(string prefix) =>
        Assert.Throws<DomainValidationException>(() => GitConnection.Create("team", prefix, Now));
}
