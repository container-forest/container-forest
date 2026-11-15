extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Tests.Commands;

public sealed class FormattingTests
{
    private static readonly ForestEnvironment Prod = new("id", "prod", true);

    [Fact]
    public void NoEnvironments_TellsTheUserToAskForAccess()
    {
        Assert.Contains("Ask an administrator", ReplyFormatter.Environments([]));
    }

    [Fact]
    public void Environments_AreSortedByName()
    {
        var text = ReplyFormatter.Environments([new("2", "web", true), new("1", "Api", true)]);

        Assert.True(text.IndexOf("Api", StringComparison.Ordinal) < text.IndexOf("web", StringComparison.Ordinal));
    }

    [Fact]
    public void Containers_AreGroupedByStack_WithContainersOutsideStacksLast()
    {
        var text = ReplyFormatter.Containers(Prod,
        [
            CommandTests.Container("scratch", null, ContainerState.Exited),
            CommandTests.Container("web-app-1", "web"),
            CommandTests.Container("db-1", "data", canControl: false),
        ]);

        Assert.Equal(
            """
            **prod** · 3 containers
            __Stack data__
            🟢 `db-1` — Up 3 minutes · view only
            __Stack web__
            🟢 `web-app-1` — Up 3 minutes
            __Outside stacks__
            🔴 `scratch` — Up 3 minutes
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void ContainerWithoutStatusText_ShowsItsState()
    {
        var container = CommandTests.Container("web", null, ContainerState.Paused) with { StatusText = "" };

        Assert.Equal("⏸️ `web` — paused", ReplyFormatter.ContainerLine(container));
    }

    [Fact]
    public void LongLists_StayWithinDiscordsLimit_AndSayHowManyAreLeftOut()
    {
        var containers = Enumerable.Range(0, 200).Select(i => CommandTests.Container($"worker-{i:000}", "jobs")).ToList();

        var text = ReplyFormatter.Containers(Prod, containers);

        Assert.True(text.Length <= ReplyFormatter.MaxMessageLength, $"{text.Length} characters");
        Assert.Matches(@"…and \d+ more lines\.$", text);
        var shown = text.Split('\n').Count(l => l.Contains("`worker-", StringComparison.Ordinal));
        var omitted = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"and (\d+) more").Groups[1].Value);
        Assert.Equal(200, shown + omitted);
    }

    [Theory]
    [InlineData("web_app*1", @"web\_app\*1")]
    [InlineData("~~x~~ `y` |z| > q", @"\~\~x\~\~ \`y\` \|z\| \> q")]
    [InlineData("[link](http://x) #h", @"\[link\]\(http://x\) \#h")]
    [InlineData(@"a\b", @"a\\b")]
    public void Escape_KeepsMarkdownLiteral(string text, string expected)
    {
        Assert.Equal(expected, Markdown.Escape(text));
    }

    [Fact]
    public void Code_DropsBackticks()
    {
        Assert.Equal("`web`", Markdown.Code("we`b"));
    }

    [Fact]
    public void Environments_EscapeNames()
    {
        Assert.Contains(@"**my\_env**", ReplyFormatter.Environments([new("1", "my_env", true)]));
    }

    [Fact]
    public void Stats_ShowTheHostAndTheBusiestContainers()
    {
        var stats = new ForestStats(
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            new ForestHostStats(true, 4, 0.5, 1.25, 8UL << 30, 2UL << 30, [new ForestFilesystemStats("/", 100UL << 30, 0.42)]),
            [
                new ForestContainerStats("idle-1", "web", true, 0.01, 10UL << 20, 0),
                new ForestContainerStats("busy-1", "web", true, 1.5, 256UL << 20, 512UL << 20),
            ]);

        var text = ReplyFormatter.Stats(Prod, null, stats);

        Assert.Equal(
            """
            **prod** · <t:1800000000:R>
            🖥️ CPU 50% of 4 cores · load 1.25
            🧠 Memory 2.0GiB / 8.0GiB (25%)
            💾 `/` 42% of 100GiB
            __2 containers__
            `busy-1` CPU 150% · 256MiB / 512MiB
            `idle-1` CPU 1.0% · 10.0MiB
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Stats_WithoutASample_SayWhy()
    {
        Assert.Contains("no resource usage in the last hour", ReplyFormatter.Stats(Prod, "web", new ForestStats(null, null, [])));
    }
}
