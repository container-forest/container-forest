using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Domain.Tests.Repositories;

public class GitUrlTests
{
    [Theory]
    [InlineData("https://git.example.com/", "https://git.example.com/team/app.git")]
    [InlineData("https://git.example.com", "https://git.example.com/team/app.git")]
    [InlineData("https://git.example.com/team/", "https://git.example.com/team/app.git")]
    [InlineData("https://git.example.com/team", "https://git.example.com/TEAM/App")]
    [InlineData("https://git.example.com/team/app.git", "https://git.example.com/team/app")]
    [InlineData("https://git.example.com/team/app", "https://git.example.com/team/app.git/info/refs")]
    [InlineData("https://GIT.example.com:443/team/", "https://git.example.com/team/app.git")]
    public void Covers_urls_under_the_prefix(string prefix, string url) =>
        Assert.True(GitUrl.Covers(new Uri(prefix), new Uri(url)));

    [Theory]
    [InlineData("https://git.example.com/team/", "https://git.example.com/team-b/app.git")]
    [InlineData("https://git.example.com/team/", "https://git.example.com/app.git")]
    [InlineData("https://git.example.com/team/", "https://other.example.com/team/app.git")]
    [InlineData("https://git.example.com/team/", "https://git.example.com:8443/team/app.git")]
    [InlineData("https://git.example.com/team/", "http://git.example.com/team/app.git")]
    [InlineData("https://git.example.com/team/", "https://git.example.com/team/../other/app.git")]
    public void Does_not_cover_urls_outside_the_prefix(string prefix, string url) =>
        Assert.False(GitUrl.Covers(new Uri(prefix), new Uri(url)));

    [Theory]
    [InlineData("https://git.example.com/Team/App.git", "https://git.example.com/team/app")]
    [InlineData("https://git.example.com/team/app/", "https://GIT.EXAMPLE.COM/team/app.git")]
    public void The_key_is_the_same_for_every_spelling(string a, string b) =>
        Assert.Equal(GitUrl.Key(new Uri(a)), GitUrl.Key(new Uri(b)));

    [Fact]
    public void Different_repositories_have_different_keys() =>
        Assert.NotEqual(
            GitUrl.Key(new Uri("https://git.example.com/team/app.git")),
            GitUrl.Key(new Uri("https://git.example.com/team/app2.git")));
}
