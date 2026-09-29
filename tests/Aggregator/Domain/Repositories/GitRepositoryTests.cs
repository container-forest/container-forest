using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Repositories;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Repositories;

public class GitRepositoryTests
{
    [Fact]
    public void Accepts_an_https_url()
    {
        var repository = GitRepository.Create("infra", "https://git.example.com/team/infra.git", "protected", Now);

        Assert.Equal("git.example.com", repository.Url.Host);
    }

    [Theory]
    [InlineData("http://git.example.com/team/infra.git")]
    [InlineData("git@github.com:team/infra.git")]
    [InlineData("ssh://git@github.com/team/infra.git")]
    [InlineData("infra.git")]
    public void Rejects_non_https_urls(string url) =>
        Assert.Throws<DomainValidationException>(() => GitRepository.Create("infra", url, null, Now));

    [Fact]
    public void Rejects_credentials_in_the_url() =>
        Assert.Throws<DomainValidationException>(
            () => GitRepository.Create("infra", "https://user:token@git.example.com/team/infra.git", null, Now));
}
