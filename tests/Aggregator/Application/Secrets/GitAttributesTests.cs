using ContainerForest.Aggregator.Application.Secrets;

namespace ContainerForest.Aggregator.Application.Tests.Secrets;

public class GitAttributesTests
{
    [Theory]
    [InlineData(".env", true)]
    [InlineData("apps/web/.env", true)]
    [InlineData("apps/web/.env.example", false)]
    [InlineData("secrets/prod/db.yaml", true)]
    [InlineData("secrets/README.md", false)]
    [InlineData("apps/secrets/db.yaml", false)]
    [InlineData("config/app.secret.json", true)]
    public void Patterns_follow_gitignore_rules(string path, bool filtered)
    {
        var attributes = new GitAttributes([new GitAttributesFile("", """
            # secrets of every stack
            .env                filter=forest diff=forest
            /secrets/**         filter=forest diff=forest
            secrets/README.md   -filter
            *.secret.json       filter=forest
            """)]);

        Assert.Equal(filtered, attributes.IsFiltered(path));
    }

    [Fact]
    public void Deeper_file_and_later_line_win()
    {
        var attributes = new GitAttributes([
            new GitAttributesFile("apps", "public.env !filter\n"),
            new GitAttributesFile("", "*.env filter=forest\n"),
        ]);

        Assert.True(attributes.IsFiltered("apps/private.env"));
        Assert.False(attributes.IsFiltered("apps/public.env"));
        Assert.True(attributes.IsFiltered("public.env"));
    }

    [Fact]
    public void Another_driver_and_unrelated_lines_do_not_count()
    {
        var attributes = new GitAttributes([new GitAttributesFile("", "*.env filter=lfs\n*.png binary\n[attr]x filter=forest\n")]);

        Assert.False(attributes.IsFiltered("a.env"));
        Assert.False(attributes.IsFiltered("a.png"));
    }
}
