using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Connection;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Agent.Tests.Commands;

public class StreamContainerLogsHandlerTests
{
    private readonly FakeDockerGateway docker = new();
    private readonly RecordingProgress progress = new();
    private readonly ManualTimeProvider time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Handler_handles_stream_container_logs()
    {
        Assert.Equal(ControllerMessage.BodyOneofCase.StreamContainerLogs, Handler().Handles);
    }

    [Fact]
    public async Task Lines_are_sent_in_batches_then_the_result()
    {
        AddLines(LogBatcher.MaxLines + 5);

        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1", TailLines = 300 }), progress, Ct);

        Assert.Null(result.Error);
        Assert.Equal("r", result.RequestId);
        Assert.Equal([LogBatcher.MaxLines, 5], progress.Batches.Select(b => b.Count));
        Assert.Equal("line 0", progress.Batches[0][0].Text);
        Assert.Equal("line 204", progress.Batches[1][^1].Text);
    }

    [Fact]
    public async Task Tail_and_since_reach_docker()
    {
        var since = DateTimeOffset.Parse("2026-10-02T12:00:00.250Z");

        await Handler().HandleAsync(
            Message(new StreamContainerLogs { ContainerId = "web-1", TailLines = 20, SinceUnixMs = since.ToUnixTimeMilliseconds() }), progress, Ct);

        Assert.Equal($"logs web-1 tail=20 since={since.ToUnixTimeMilliseconds()}", Assert.Single(docker.Calls));
    }

    [Fact]
    public async Task Zero_since_means_no_lower_bound()
    {
        await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1" }), progress, Ct);

        Assert.Equal("logs web-1 tail=0 since=", Assert.Single(docker.Calls));
    }

    [Fact]
    public async Task Empty_log_sends_no_batch()
    {
        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1", TailLines = 10 }), progress, Ct);

        Assert.Null(result.Error);
        Assert.Empty(progress.Batches);
    }

    [Theory]
    [InlineData("../images/create")]
    [InlineData("c1/logs")]
    [InlineData("")]
    public async Task Invalid_container_ids_never_reach_docker(string containerId)
    {
        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = containerId }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Negative_tail_is_an_invalid_argument()
    {
        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1", TailLines = -1 }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Follow_is_not_supported_yet()
    {
        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1", Follow = true }), progress, Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error.Code);
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Missing_container_is_not_found()
    {
        docker.Failure = new ContainerNotFoundException("web-9");

        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-9" }), progress, Ct);

        Assert.Equal(ErrorCode.NotFound, result.Error.Code);
        Assert.Equal("Container 'web-9' was not found.", result.Error.Message);
    }

    [Fact]
    public async Task Docker_unavailable_is_reported_without_details()
    {
        docker.Failure = new DockerUnavailableException(new IOException("pipe not found"));

        var result = await Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1" }), progress, Ct);

        Assert.Equal(ErrorCode.DockerUnavailable, result.Error.Code);
        Assert.DoesNotContain("pipe", result.Error.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        AddLines(3);
        docker.LogGate = new TaskCompletionSource();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var handling = Handler().HandleAsync(Message(new StreamContainerLogs { ContainerId = "web-1" }), progress, cancel.Token);
        await cancel.CancelAsync();
        var result = await handling;

        Assert.Equal(ErrorCode.Cancelled, result.Error.Code);
    }

    [Fact]
    public async Task Lines_go_out_as_log_batches_of_the_request()
    {
        AddLines(2);
        var stream = new RecordingStreamWriter();
        using var writer = new AgentStreamWriter(stream);

        await Handler().HandleAsync(
            new ControllerMessage { RequestId = "r7", StreamContainerLogs = new StreamContainerLogs { ContainerId = "web-1" } },
            new StreamCommandProgress("r7", writer),
            Ct);

        var batch = (await stream.NextAsync()).LogBatch;
        Assert.Equal("r7", batch.RequestId);
        Assert.Equal(["line 0", "line 1"], batch.Lines.Select(l => l.Text));
    }

    private StreamContainerLogsHandler Handler() => new(docker, time, NullLogger<StreamContainerLogsHandler>.Instance);

    private void AddLines(int count)
    {
        for (var i = 0; i < count; i++)
        {
            docker.LogLines.Add(new LogLine { Stream = LogStream.Stdout, TimestampUnixMs = 1_000 + i, Text = $"line {i}" });
        }
    }

    private static ControllerMessage Message(StreamContainerLogs command) => new() { RequestId = "r", StreamContainerLogs = command };
}
