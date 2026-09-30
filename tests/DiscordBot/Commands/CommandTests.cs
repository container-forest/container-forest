extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Tests.Forest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.DiscordBot.Tests.Commands;

public sealed class CommandTests
{
    private const string ProdId = "0199a1b2-0000-7000-8000-000000000001";
    private const string LabId = "0199a1b2-0000-7000-8000-000000000002";

    private readonly StubForestClient forest = new();

    public CommandTests()
    {
        forest.Environments.Add(new ForestEnvironment(ProdId, "prod", Online: true));
        forest.Environments.Add(new ForestEnvironment(LabId, "lab", Online: false));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EnvironmentResolver Resolver => new(forest);

    [Fact]
    public async Task Environments_DefersFirst_ThenAnswersWithTheList()
    {
        var interaction = new RecordingInteraction(forest);

        await new EnvironmentsCommand(forest, NullLogger<EnvironmentsCommand>.Instance).ExecuteAsync(interaction, default, Ct);

        Assert.Equal(["defer after 0 calls", "respond"], interaction.Events);
        Assert.Equal([$"environments {RecordingInteraction.Dmytro.Id}"], forest.Calls);
        var reply = interaction.Reply!;
        Assert.False(reply.IsError);
        Assert.Contains("🟢 **prod** — online", reply.Content);
        Assert.Contains("🔴 **lab** — agent offline", reply.Content);
    }

    [Fact]
    public async Task Containers_ResolvesTheEnvironmentByName()
    {
        forest.Containers[ProdId] = [Container("web-app-1", "web")];
        var interaction = new RecordingInteraction(forest);

        await new ContainersCommand(forest, Resolver, NullLogger<ContainersCommand>.Instance).ExecuteAsync(interaction, " PROD ", Ct);

        Assert.Contains($"containers {RecordingInteraction.Dmytro.Id} {ProdId}", forest.Calls);
        Assert.Contains("`web-app-1`", interaction.Reply!.Content);
    }

    [Fact]
    public async Task Containers_ResolvesTheEnvironmentById()
    {
        var interaction = new RecordingInteraction(forest);

        await new ContainersCommand(forest, Resolver, NullLogger<ContainersCommand>.Instance).ExecuteAsync(interaction, LabId, Ct);

        Assert.Contains($"containers {RecordingInteraction.Dmytro.Id} {LabId}", forest.Calls);
        Assert.Equal("**lab** · 0 containers", interaction.Reply!.Content);
    }

    [Fact]
    public async Task UnknownEnvironment_IsAnErrorWithoutCallingForContainers()
    {
        var interaction = new RecordingInteraction(forest);

        await new ContainersCommand(forest, Resolver, NullLogger<ContainersCommand>.Instance).ExecuteAsync(interaction, "staging", Ct);

        Assert.True(interaction.Reply!.IsError);
        Assert.Contains("staging", interaction.Reply.Content);
        Assert.DoesNotContain(forest.Calls, c => c.StartsWith("containers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("restart", 30, "restart 238471902384719872 " + ProdId + " web-app-1 30", "✅ Restarted `web-app-1` in **prod**.")]
    [InlineData("stop", 0, "stop 238471902384719872 " + ProdId + " web-app-1 0", "✅ Stopped `web-app-1` in **prod**.")]
    [InlineData("stop", null, "stop 238471902384719872 " + ProdId + " web-app-1 default", "✅ Stopped `web-app-1` in **prod**.")]
    [InlineData("start", 30, "start 238471902384719872 " + ProdId + " web-app-1 default", "✅ Started `web-app-1` in **prod**.")]
    public async Task ContainerActions_SendTheActionAndReportIt(string action, int? timeout, string expectedCall, string expectedReply)
    {
        var interaction = new RecordingInteraction(forest);

        await ActionCommand(action).ExecuteAsync(interaction, new ContainerActionArgs("prod", " web-app-1 ", timeout), Ct);

        Assert.Equal(expectedCall, forest.Calls.Last());
        Assert.Equal(expectedReply, interaction.Reply!.Content);
    }

    public static TheoryData<ForestException> AggregatorFailures() =>
    [
        new ForestAccessDeniedException("You need the Operator role on prod."),
        new AgentUnavailableException("Agent of environment prod is offline."),
        new AccountNotLinkedException(),
        new ForestUnavailableException("diagnostic for the log only"),
    ];

    [Theory]
    [MemberData(nameof(AggregatorFailures))]
    public async Task AggregatorFailures_BecomeReadableErrors(ForestException failure)
    {
        var interaction = new RecordingInteraction(forest);
        forest.Environments.Clear();
        forest.Environments.Add(new ForestEnvironment(ProdId, "prod", true));
        var command = ActionCommand("restart");
        forest.Failure = failure;

        await command.ExecuteAsync(interaction, new ContainerActionArgs(ProdId, "web"), Ct);

        Assert.Equal(["defer after 0 calls", "respond"], interaction.Events);
        Assert.True(interaction.Reply!.IsError);
        Assert.Equal("❌ " + Markdown.Escape(failure.Message), interaction.Reply.Content);
    }

    [Fact]
    public async Task UnexpectedFailure_IsLogged_AndHiddenFromTheUser()
    {
        var logger = new RecordingLogger<EnvironmentsCommand>();
        forest.Failure = new InvalidOperationException("Sequence contains no elements at Internal.Detail()");
        var interaction = new RecordingInteraction(forest);

        await new EnvironmentsCommand(forest, logger).ExecuteAsync(interaction, default, Ct);

        Assert.Equal("❌ " + EnvironmentsCommand.UnexpectedErrorMessage, interaction.Reply!.Content);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("/environments", entry.Message);
        Assert.Contains("Internal.Detail", entry.Message);
    }

    [Fact]
    public async Task Cancellation_EndsWithoutAReply()
    {
        forest.Hold = new TaskCompletionSource();
        var interaction = new RecordingInteraction(forest);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = new EnvironmentsCommand(forest, NullLogger<EnvironmentsCommand>.Instance).ExecuteAsync(interaction, default, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(interaction.Reply);
    }

    [Fact]
    public async Task Link_SendsTheDiscordNameAndTrimmedCode()
    {
        forest.LinkedUserName = "root_admin";
        var interaction = new RecordingInteraction(forest);

        await new LinkCommand(forest, NullLogger<LinkCommand>.Instance).ExecuteAsync(interaction, "  9BACN3RC ", Ct);

        Assert.Equal([$"link {RecordingInteraction.Dmytro.Id} dmytro_s 9BACN3RC"], forest.Calls);
        Assert.Equal("🔗 Your Discord account is now linked to **root\\_admin**.", interaction.Reply!.Content);
    }

    [Fact]
    public void CommandNames_MatchTheSlashCommands()
    {
        Assert.Equal("start", ActionCommand("start").Name);
        Assert.Equal("stop", ActionCommand("stop").Name);
        Assert.Equal("restart", ActionCommand("restart").Name);
    }

    private ContainerActionCommand ActionCommand(string action) => action switch
    {
        "start" => new StartContainerCommand(forest, Resolver, NullLogger<StartContainerCommand>.Instance),
        "stop" => new StopContainerCommand(forest, Resolver, NullLogger<StopContainerCommand>.Instance),
        _ => new RestartContainerCommand(forest, Resolver, NullLogger<RestartContainerCommand>.Instance),
    };

    internal static ForestContainer Container(
        string name, string? stack, ContainerState state = ContainerState.Running, bool canControl = true) =>
        new(name + "-id-4f1c2a9e8b7d", name, state, "nginx:1.27", stack, stack is null ? null : "app", "Up 3 minutes", canControl);
}
