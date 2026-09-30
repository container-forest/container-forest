extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.DiscordBot.Tests.Commands;

public sealed class AutocompleteTests
{
    private const string ProdId = "0199a1b2-0000-7000-8000-000000000001";

    private readonly StubForestClient forest = new();

    public AutocompleteTests()
    {
        forest.Environments.Add(new ForestEnvironment(ProdId, "prod", Online: true));
        forest.Environments.Add(new ForestEnvironment("0199a1b2-0000-7000-8000-000000000002", "lab", Online: false));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AutocompleteSource Source => new(forest, NullLogger<AutocompleteSource>.Instance);

    [Fact]
    public async Task Environments_SuggestNamesWithIdsAsValues_AndMarkOfflineOnes()
    {
        var suggestions = await Source.EnvironmentsAsync(RecordingInteraction.Dmytro, "", Ct);

        Assert.Equal(
            [new Suggestion("lab (offline)", "0199a1b2-0000-7000-8000-000000000002"), new Suggestion("prod", ProdId)],
            suggestions);
    }

    [Fact]
    public async Task Environments_FilterByWhatTheUserTyped()
    {
        var suggestions = await Source.EnvironmentsAsync(RecordingInteraction.Dmytro, "PR", Ct);

        Assert.Equal("prod", Assert.Single(suggestions).Name);
    }

    [Fact]
    public void Suggestions_AreLimitedTo25()
    {
        var environments = Enumerable.Range(0, 40).Select(i => new ForestEnvironment($"id-{i}", $"env-{i:00}", true)).ToList();

        Assert.Equal(AutocompleteSource.MaxSuggestions, AutocompleteSource.SuggestEnvironments(environments, null).Count);
    }

    [Theory]
    [InlineData(null, new[] { "api", "web", "worker" })]
    [InlineData(ContainerAction.Restart, new[] { "api", "web", "worker" })]
    [InlineData(ContainerAction.Start, new[] { "worker" })]
    [InlineData(ContainerAction.Stop, new[] { "api", "web" })]
    public void Containers_FitTheAction_AndHideThoseTheUserCannotControl(ContainerAction? action, string[] expected)
    {
        ForestContainer[] containers =
        [
            CommandTests.Container("web", "site"),
            CommandTests.Container("api", "site", ContainerState.Restarting),
            CommandTests.Container("worker", "jobs", ContainerState.Exited),
            CommandTests.Container("db", "data", canControl: false),
        ];

        var suggestions = AutocompleteSource.SuggestContainers(containers, action, null);

        Assert.Equal(expected, suggestions.Select(s => s.Value));
    }

    [Fact]
    public void Containers_MatchByNameOrIdPrefix()
    {
        ForestContainer[] containers = [CommandTests.Container("web", "site") with { Id = "4f1c2a9e8b7d" }, CommandTests.Container("api", "site")];

        Assert.Equal("web", Assert.Single(AutocompleteSource.SuggestContainers(containers, null, "4f1c")).Value);
    }

    [Fact]
    public void LongNames_AreTruncated_AndSentAsIdPrefix()
    {
        var name = new string('a', 120);
        var container = CommandTests.Container(name, null) with { Id = "4f1c2a9e8b7d9a8b7c6d5e4f" };

        var suggestion = Assert.Single(AutocompleteSource.SuggestContainers([container], null, null));

        Assert.Equal(AutocompleteSource.MaxLength, suggestion.Name.Length);
        Assert.Equal("4f1c2a9e8b7d", suggestion.Value);
    }

    [Fact]
    public async Task Containers_UseTheEnvironmentChosenInTheSameCommand_ByIdOrName()
    {
        forest.Containers[ProdId] = [CommandTests.Container("web", "site")];

        var byId = await Source.ContainersAsync(RecordingInteraction.Dmytro, ProdId, null, null, Ct);
        var byName = await Source.ContainersAsync(RecordingInteraction.Dmytro, "prod", null, null, Ct);

        Assert.Equal("web", Assert.Single(byId).Value);
        Assert.Equal("web", Assert.Single(byName).Value);
    }

    [Fact]
    public async Task Containers_WithoutAnEnvironment_SuggestNothing_AndCallNothing()
    {
        Assert.Empty(await Source.ContainersAsync(RecordingInteraction.Dmytro, " ", null, null, Ct));
        Assert.Empty(forest.Calls);
    }

    [Fact]
    public async Task AggregatorFailure_MeansNoSuggestions()
    {
        forest.Failure = new AccountNotLinkedException();

        Assert.Empty(await Source.EnvironmentsAsync(RecordingInteraction.Dmytro, null, Ct));
        Assert.Empty(await Source.ContainersAsync(RecordingInteraction.Dmytro, "prod", null, null, Ct));
    }

    [Fact]
    public async Task SlowAggregator_MeansNoSuggestions_WithinTheBudget()
    {
        forest.Hold = new TaskCompletionSource();
        var started = DateTime.UtcNow;

        var suggestions = await Source.EnvironmentsAsync(RecordingInteraction.Dmytro, null, Ct);

        Assert.Empty(suggestions);
        Assert.True(DateTime.UtcNow - started < AutocompleteSource.Budget + TimeSpan.FromSeconds(1));
    }
}
