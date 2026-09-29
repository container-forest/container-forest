using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Tests.Stacks;

public class StackNameTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("0day")]
    [InlineData("my_app-2")]
    public void Accepts_valid_names(string value) => Assert.Equal(value, StackName.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Web")]
    [InlineData("-web")]
    [InlineData("_web")]
    [InlineData("web app")]
    [InlineData("../etc")]
    [InlineData("web/app")]
    [InlineData("web\n")]
    public void Rejects_invalid_names(string value)
    {
        Assert.False(StackName.TryParse(value, out _));
        Assert.Throws<DomainValidationException>(() => StackName.Parse(value));
    }

    [Fact]
    public void Rejects_names_longer_than_limit()
    {
        Assert.True(StackName.TryParse(new string('a', StackName.MaxLength), out _));
        Assert.False(StackName.TryParse(new string('a', StackName.MaxLength + 1), out _));
    }

    [Fact]
    public void Equal_names_are_equal() => Assert.Equal(StackName.Parse("web"), StackName.Parse("web"));
}

public class StackFilePathTests
{
    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("stacks/web/compose.yaml")]
    [InlineData("config/.env.example")]
    public void Accepts_relative_paths(string value) => Assert.Equal(value, StackFilePath.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../secret")]
    [InlineData("config/../../secret")]
    [InlineData("./compose.yaml")]
    [InlineData("config//app.yaml")]
    [InlineData("config/")]
    [InlineData("config\\app.yaml")]
    [InlineData("C:/app.yaml")]
    [InlineData("app\0.yaml")]
    public void Rejects_paths_that_could_escape_or_are_malformed(string value) =>
        Assert.False(StackFilePath.TryParse(value, out _));
}

public class CommitShaTests
{
    [Fact]
    public void Accepts_sha1_and_normalizes_case()
    {
        var sha = CommitSha.Parse("ABCDEF0123456789ABCDEF0123456789ABCDEF01");

        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", sha.Value);
        Assert.Equal("abcdef0", sha.Short);
    }

    [Fact]
    public void Accepts_sha256() => Assert.True(CommitSha.TryParse(new string('a', 64), out _));

    [Theory]
    [InlineData("abcdef0")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("")]
    public void Rejects_short_or_non_hex(string value) => Assert.False(CommitSha.TryParse(value, out _));
}

public class BranchNameTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/agent-contract")]
    [InlineData("release-1.2")]
    public void Accepts_valid_branches(string value) => Assert.Equal(value, BranchName.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("-main")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("main.lock")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("a/.hidden")]
    [InlineData("main@{1}")]
    [InlineData("@")]
    [InlineData("my branch")]
    [InlineData("a:b")]
    public void Rejects_invalid_branches(string value) => Assert.False(BranchName.TryParse(value, out _));
}
