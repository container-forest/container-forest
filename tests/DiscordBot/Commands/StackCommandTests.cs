extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.DiscordBot.Tests.Commands;

public sealed class StackCommandTests
{
    private const string ProdId = "0199a1b2-0000-7000-8000-000000000001";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly ForestEnvironment Prod = new(ProdId, "prod", Online: true);

    private readonly StubForestClient forest = new();

    public StackCommandTests() => forest.Environments.Add(Prod);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EnvironmentResolver Resolver => new(forest);

    private RestartStackCommand Restart => new(forest, Resolver, NullLogger<RestartStackCommand>.Instance);

    private DownStackCommand Down => new(forest, Resolver, NullLogger<DownStackCommand>.Instance);

    [Fact]
    public async Task Restart_DefersFirst_SendsTheStackAndTimeout_AndShowsTheOutput()
    {
        forest.StackResult = new ForestStackResult(null, [" Container web-app-1 Restarting ", " Container web-app-1 Started "]);
        var interaction = new RecordingInteraction(forest);

        await Restart.ExecuteAsync(interaction, new StackActionArgs("prod", " web ", 30), Ct);

        Assert.Equal(["defer after 0 calls", "respond"], interaction.Events);
        Assert.Contains($"stack-restart {RecordingInteraction.Dmytro.Id} {ProdId} web 30", forest.Calls);
        Assert.False(interaction.Reply!.IsError);
        Assert.Equal(
            "✅ Restarted stack `web` in **prod**.\n```\nContainer web-app-1 Restarting\nContainer web-app-1 Started\n```",
            interaction.Reply.Content);
    }

    [Fact]
    public async Task Down_WithoutTimeout_LeavesItToTheAggregator()
    {
        var interaction = new RecordingInteraction(forest);

        await Down.ExecuteAsync(interaction, new StackActionArgs(ProdId, "web"), Ct);

        Assert.Contains($"stack-down {RecordingInteraction.Dmytro.Id} {ProdId} web default", forest.Calls);
        Assert.Equal("✅ Took down stack `web` in **prod**.", interaction.Reply!.Content);
    }

    [Fact]
    public async Task FailedCompose_IsAnErrorReply_WithTheEscapedErrorAndTheOutput()
    {
        forest.StackResult = new ForestStackResult(
            "docker compose down exited with code 1; see the command output.", ["network web_default has active endpoints"]);
        var interaction = new RecordingInteraction(forest);

        await Down.ExecuteAsync(interaction, new StackActionArgs("prod", "web"), Ct);

        Assert.True(interaction.Reply!.IsError);
        Assert.StartsWith("❌ Stack `web` in **prod**: docker compose down exited with code 1", interaction.Reply.Content);
        Assert.EndsWith("```\nnetwork web_default has active endpoints\n```", interaction.Reply.Content);
    }

    [Fact]
    public async Task RejectedAction_ShowsTheAggregatorsMessage()
    {
        forest.Failure = new ForestAccessDeniedException("Only the Editor role or higher may take down stack 'web'.");
        var interaction = new RecordingInteraction(forest);

        await Down.ExecuteAsync(interaction, new StackActionArgs(ProdId, "web"), Ct);

        Assert.True(interaction.Reply!.IsError);
        Assert.Contains("Only the Editor role", interaction.Reply.Content);
    }

    [Fact]
    public async Task Stacks_ListsTheEnvironmentsStacks()
    {
        forest.Stacks[ProdId] = [Stack("web", canTakeDown: false), Stack("api"), Stack("forest", isSystem: true), Stack("new", deployed: false)];
        var interaction = new RecordingInteraction(forest);

        await new StacksCommand(forest, Resolver, NullLogger<StacksCommand>.Instance).ExecuteAsync(interaction, "prod", Ct);

        Assert.Equal(
            """
            **prod** · 4 stacks
            📦 `api` — deployed `0123456789ab` · restart, down
            📦 `forest` — deployed `0123456789ab` · system stack
            ⚪ `new` — not deployed · restart, down
            📦 `web` — deployed `0123456789ab` · restart
            """.ReplaceLineEndings("\n"),
            interaction.Reply!.Content);
    }

    [Theory]
    [InlineData(null, new[] { "api", "web" })]
    [InlineData(StackAction.Restart, new[] { "api", "web" })]
    [InlineData(StackAction.Down, new[] { "api" })]
    public void StackSuggestions_FitTheAction_AndSkipStacksNeverDeployed(StackAction? action, string[] expected)
    {
        ForestStack[] stacks =
        [
            Stack("web", canTakeDown: false),
            Stack("api"),
            Stack("forest", isSystem: true, canRestart: false, canTakeDown: false),
            Stack("new", deployed: false),
            Stack("docs", canRestart: false, canTakeDown: false),
        ];

        Assert.Equal(expected, AutocompleteSource.SuggestStacks(stacks, action, null).Select(s => s.Value));
        Assert.Equal(["api"], AutocompleteSource.SuggestStacks(stacks, action, "AP").Select(s => s.Value));
    }

    [Fact]
    public async Task StackSuggestions_NeedAnEnvironment()
    {
        var source = new AutocompleteSource(forest, NullLogger<AutocompleteSource>.Instance);
        forest.Stacks[ProdId] = [Stack("web")];

        Assert.Empty(await source.StacksAsync(RecordingInteraction.Dmytro, null, StackAction.Restart, null, Ct));
        Assert.Equal(["web"], (await source.StacksAsync(RecordingInteraction.Dmytro, "prod", StackAction.Restart, null, Ct)).Select(s => s.Value));
    }

    [Fact]
    public void Output_KeepsTheLastLinesThatFit_AndSaysHowManyAreLeftOut()
    {
        var output = Enumerable.Range(1, 500).Select(i => $"Container worker-{i:000} Stopped").ToList();

        var text = ReplyFormatter.WithOutput("✅ Took down stack `jobs` in **prod**.", output);

        Assert.True(text.Length <= ReplyFormatter.MaxMessageLength, $"{text.Length} characters");
        Assert.EndsWith("Container worker-500 Stopped\n```", text);
        var omitted = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"… (\d+) earlier lines").Groups[1].Value);
        var shown = text.Split('\n').Count(l => l.StartsWith("Container worker-", StringComparison.Ordinal));
        Assert.Equal(500, shown + omitted);
    }

    [Fact]
    public void Output_CannotCloseTheCodeBlock()
    {
        var text = ReplyFormatter.WithOutput("header", ["```", "@everyone **bold**"]);

        Assert.Equal(2, text.Split("```").Length - 1);
        Assert.Contains("@everyone **bold**", text);
    }

    [Fact]
    public void Output_WithAnOverlongLastLine_KeepsItsEnd()
    {
        var text = ReplyFormatter.WithOutput("header", ["first", new string('x', 3000) + "END"]);

        Assert.True(text.Length <= ReplyFormatter.MaxMessageLength, $"{text.Length} characters");
        Assert.EndsWith("END\n```", text);
    }

    [Fact]
    public void EmptyOutput_IsJustTheHeader()
    {
        Assert.Equal("header", ReplyFormatter.WithOutput("header", ["  ", ""]));
    }

    private static ForestStack Stack(
        string name, bool deployed = true, bool isSystem = false, bool canRestart = true, bool canTakeDown = true) =>
        new($"id-{name}", name, isSystem, deployed ? Commit : null, deployed ? DateTimeOffset.UnixEpoch : null, canRestart, canTakeDown);
}
