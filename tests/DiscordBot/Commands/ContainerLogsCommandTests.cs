extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.DiscordBot.Tests.Commands;

public sealed class ContainerLogsCommandTests
{
    private const string ProdId = "0199a1b2-0000-7000-8000-000000000001";

    private readonly StubForestClient forest = new();

    public ContainerLogsCommandTests() => forest.Environments.Add(new ForestEnvironment(ProdId, "prod", Online: true));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logs_DefersFirst_ThenShowsTheLinesInACodeBlock()
    {
        forest.LogLines.Add(new ForestLogLine(DateTimeOffset.UnixEpoch, false, "listening on :8080"));
        forest.LogLines.Add(new ForestLogLine(null, true, "\u001b[31mpanic\u001b[0m: boom\r"));
        var interaction = new RecordingInteraction(forest);

        var reply = await Command().ExecuteAsync(interaction, new ContainerActionArgs(ProdId, "web-app-1"), Ct);

        Assert.Equal(["defer after 0 calls", "respond"], interaction.Events);
        Assert.Contains($"logs {RecordingInteraction.Dmytro.Id} {ProdId} web-app-1 {ContainerLogsCommand.TailLines}", forest.Calls);
        Assert.False(reply.IsError);
        Assert.Equal("📜 Log of `web-app-1` in **prod**:\n```\nlistening on :8080\npanic: boom\n```", reply.Content);
    }

    [Fact]
    public async Task EmptyLog_SaysSo()
    {
        forest.LogLines.Add(new ForestLogLine(null, false, "   "));

        var reply = await Command().ExecuteAsync(new RecordingInteraction(forest), new ContainerActionArgs(ProdId, "web-app-1"), Ct);

        Assert.Equal("📜 Log of `web-app-1` in **prod**: no lines.", reply.Content);
    }

    [Fact]
    public async Task LongLog_KeepsItsEnd_WithinOneMessage()
    {
        for (var i = 0; i < ContainerLogsCommand.TailLines; i++)
        {
            forest.LogLines.Add(new ForestLogLine(null, false, $"line {i} " + new string('x', 200)));
        }

        var reply = await Command().ExecuteAsync(new RecordingInteraction(forest), new ContainerActionArgs(ProdId, "web-app-1"), Ct);

        Assert.True(reply.Content.Length <= ReplyFormatter.MaxMessageLength);
        Assert.Contains($"line {ContainerLogsCommand.TailLines - 1} ", reply.Content);
        Assert.Contains("earlier lines", reply.Content);
    }

    [Fact]
    public async Task Failure_IsAnError_ThatTheButtonDoesNotTreatAsDone()
    {
        forest.Failure = new ForestNotFoundException("Container 'web-app-1' was not found.");

        var reply = await Command().ExecuteAsync(new RecordingInteraction(forest), new ContainerActionArgs(ProdId, "web-app-1"), Ct);

        Assert.True(reply.IsError);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("\u001b[1;32mOK\u001b[0m done", "OK done")]
    [InlineData("tab\there", "tab\there")]
    [InlineData("bell\u0007 and\u001b7 save", "bell and save")]
    public void Readable_DropsColoursAndControlCharacters(string text, string expected)
    {
        Assert.Equal(expected, ContainerLogsCommand.Readable(text));
    }

    [Fact]
    public void Readable_CutsALongLine()
    {
        var line = ContainerLogsCommand.Readable(new string('a', 20_000));

        Assert.Equal(ContainerLogsCommand.MaxLineLength, line.Length);
        Assert.EndsWith("…", line);
    }

    private ContainerLogsCommand Command() =>
        new(forest, new EnvironmentResolver(forest), NullLogger<ContainerLogsCommand>.Instance);
}
