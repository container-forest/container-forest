using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Repositories;

public class GitRepositoryTests
{
    private static readonly EnvironmentId Production = EnvironmentId.New();
    private static readonly EnvironmentId Staging = EnvironmentId.New();

    [Fact]
    public void Webhook_secret_is_set_replaced_and_removed()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", null, Now);
        Assert.False(repository.HasWebhook);

        repository.SetWebhookSecret("first");
        repository.SetWebhookSecret("second");
        Assert.Equal("second", repository.ProtectedWebhookSecret);

        repository.RemoveWebhookSecret();
        Assert.False(repository.HasWebhook);
        Assert.Throws<DomainValidationException>(() => repository.SetWebhookSecret(" "));
    }

    [Fact]
    public void Accepts_an_https_url()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", null, Now);

        Assert.Equal("git.example.com", repository.Url.Host);
        Assert.Null(repository.ConnectionId);
    }

    [Theory]
    [InlineData("http://git.example.com/team/infra.git")]
    [InlineData("git@github.com:team/infra.git")]
    [InlineData("ssh://git@github.com/team/infra.git")]
    [InlineData("infra.git")]
    [InlineData("https://deploy:secret@git.example.com/team/infra.git")]
    [InlineData("https://git.example.com/team/infra.git?ref=main")]
    [InlineData("https://git.example.com/team%2Fother/infra.git")]
    public void Rejects_unsupported_urls(string url) =>
        Assert.Throws<DomainValidationException>(() => GitRepository.Create("infra", url, null, Now));

    [Fact]
    public void A_repository_of_a_connection_lies_under_its_prefix()
    {
        var connection = GitConnection.Create("team", "https://git.example.com/team/", Now);

        var repository = GitRepository.Create("infra", "https://git.example.com/Team/infra.git", connection, Now);

        Assert.Equal(connection.Id, repository.ConnectionId);
        Assert.Throws<DomainValidationException>(() =>
            GitRepository.Create("other", "https://git.example.com/team-b/infra.git", connection, Now));
        Assert.Throws<DomainValidationException>(() =>
            GitRepository.Create("other", "https://other.example.com/team/infra.git", connection, Now));
    }

    [Fact]
    public void Dot_segments_are_resolved_before_the_prefix_is_checked()
    {
        var connection = GitConnection.Create("team", "https://git.example.com/team/", Now);

        Assert.Throws<DomainValidationException>(() =>
            GitRepository.Create("other", "https://git.example.com/team/%2e%2e/other/infra.git", connection, Now));
        Assert.Throws<DomainValidationException>(() =>
            GitRepository.Create("other", "https://git.example.com/team/../other/infra.git", connection, Now));
    }

    [Fact]
    public void Changing_the_source_keeps_the_url_under_the_new_connection()
    {
        var team = GitConnection.Create("team", "https://git.example.com/team/", Now);
        var other = GitConnection.Create("other", "https://git.example.com/other/", Now);
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", team, Now);

        Assert.Throws<DomainValidationException>(() => repository.ChangeSource("https://git.example.com/other/infra.git", team));
        Assert.Equal(team.Id, repository.ConnectionId);

        repository.ChangeSource("https://git.example.com/other/infra.git", other);
        Assert.Equal(other.Id, repository.ConnectionId);

        repository.ChangeSource("https://github.com/public/infra.git", null);
        Assert.Null(repository.ConnectionId);
    }

    [Fact]
    public void A_public_repository_is_usable_everywhere()
    {
        var repository = GitRepository.Create("infra", "https://github.com/team/infra.git", null, Now);

        Assert.True(repository.IsUsableIn(Production, null));
        Assert.True(repository.IsUsableIn(Staging, null));
    }

    [Fact]
    public void A_repository_of_a_connection_is_usable_only_where_the_connection_is_allowed()
    {
        var connection = GitConnection.Create("team", "https://git.example.com/team/", Now);
        connection.AllowEnvironments(false, [Production]);
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", connection, Now);

        Assert.True(repository.IsUsableIn(Production, connection));
        Assert.False(repository.IsUsableIn(Staging, connection));
    }

    [Fact]
    public void Usability_is_checked_against_the_repository_s_own_connection_only()
    {
        var connection = GitConnection.Create("team", "https://git.example.com/team/", Now);
        var everywhere = GitConnection.Create("all", "https://git.example.com/", Now);
        everywhere.AllowEnvironments(true, []);
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", connection, Now);

        Assert.Throws<ArgumentException>(() => repository.IsUsableIn(Production, everywhere));
        Assert.Throws<ArgumentException>(() => repository.IsUsableIn(Production, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void Rejects_invalid_names(string name) =>
        Assert.Throws<DomainValidationException>(() => GitRepository.Create(name, "https://github.com/a/b.git", null, Now));
}
