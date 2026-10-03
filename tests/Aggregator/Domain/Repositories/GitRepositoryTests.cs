using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Repositories;

public class GitRepositoryTests
{
    [Fact]
    public void Accepts_an_https_url()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);

        Assert.Equal("git.example.com", repository.Url.Host);
    }

    [Theory]
    [InlineData("http://git.example.com/team/infra.git")]
    [InlineData("git@github.com:team/infra.git")]
    [InlineData("ssh://git@github.com/team/infra.git")]
    [InlineData("infra.git")]
    public void Rejects_non_https_urls(string url) =>
        Assert.Throws<DomainValidationException>(() => GitRepository.Create("infra", url, Now));

    [Fact]
    public void Rejects_credentials_in_the_url() =>
        Assert.Throws<DomainValidationException>(
            () => GitRepository.Create("infra", "https://user:token@git.example.com/team/infra.git", Now));

    [Fact]
    public void Keeps_the_token_with_a_url_on_the_same_host()
    {
        var repository = WithToken();

        repository.ChangeUrl("https://git.example.com/team/renamed.git");

        Assert.True(repository.HasAccessToken);
        Assert.Equal("/team/renamed.git", repository.Url.AbsolutePath);
    }

    [Theory]
    [InlineData("https://evil.example.com/team/infra.git")]
    [InlineData("https://git.example.com:8443/team/infra.git")]
    public void Does_not_move_the_token_to_another_host(string url)
    {
        var repository = WithToken();

        Assert.Throws<DomainValidationException>(() => repository.ChangeUrl(url));
        Assert.Equal("git.example.com", repository.Url.Authority);
    }

    [Fact]
    public void Moves_to_another_host_after_the_token_is_removed()
    {
        var repository = WithToken();

        repository.RemoveAccessToken();
        repository.ChangeUrl("https://other.example.com/team/infra.git");

        Assert.False(repository.HasAccessToken);
        Assert.Null(repository.AccessUserName);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" gitlab+deploy-token-1 ", "gitlab+deploy-token-1")]
    public void Normalizes_the_access_user_name(string? userName, string? expected)
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);

        repository.SetAccessToken(userName, "protected");

        Assert.Equal(expected, repository.AccessUserName);
    }

    [Theory]
    [InlineData("user:name")]
    [InlineData("user\nname")]
    public void Rejects_access_user_names_that_break_basic_authentication(string userName)
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);

        Assert.Throws<DomainValidationException>(() => repository.SetAccessToken(userName, "protected"));
        Assert.False(repository.HasAccessToken);
    }

    [Fact]
    public void Rejects_an_empty_token()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);

        Assert.Throws<DomainValidationException>(() => repository.SetAccessToken(null, " "));
    }

    private static GitRepository WithToken()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", Now);
        repository.SetAccessToken("deploy", "protected");
        return repository;
    }
}
