using System.Threading.Channels;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Agents;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Agents;

public class AgentSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingWriter writer = new();
    private readonly AgentSession session;

    public AgentSessionTests() =>
        session = new AgentSession(EnvironmentId.New(), writer, Now, NullLogger.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_completes_with_the_matching_result()
    {
        var request = session.RequestAsync(ListContainers(), Timeout, Ct);
        var sent = await writer.NextAsync();

        session.Complete(new CommandResult { RequestId = sent.RequestId, Containers = new ContainerList() });

        var result = await request;
        Assert.Equal(sent.RequestId, result.RequestId);
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public async Task Results_arriving_out_of_order_complete_the_right_requests()
    {
        var first = session.RequestAsync(ListContainers(), Timeout, Ct);
        var second = session.RequestAsync(ListContainers(), Timeout, Ct);
        var sentFirst = await writer.NextAsync();
        var sentSecond = await writer.NextAsync();

        session.Complete(new CommandResult { RequestId = sentSecond.RequestId });
        session.Complete(new CommandResult { RequestId = sentFirst.RequestId });

        Assert.Equal(sentFirst.RequestId, (await first).RequestId);
        Assert.Equal(sentSecond.RequestId, (await second).RequestId);
    }

    [Fact]
    public async Task Progress_reaches_its_request_until_the_result_arrives()
    {
        var lines = new List<string>();
        var request = session.RequestAsync(ListContainers(), Timeout, Ct, lines.Add);
        var sent = await writer.NextAsync();

        session.ReportProgress(new CommandProgress { RequestId = sent.RequestId, Line = "Pulling web" });
        session.ReportProgress(new CommandProgress { RequestId = "someone-else", Line = "not ours" });
        session.Complete(new CommandResult { RequestId = sent.RequestId });
        await request;
        session.ReportProgress(new CommandProgress { RequestId = sent.RequestId, Line = "too late" });

        Assert.Equal(["Pulling web"], lines);
    }

    [Fact]
    public async Task Log_batches_reach_their_request_until_the_result_arrives()
    {
        var batches = new List<LogBatch>();
        var request = session.RequestAsync(ListContainers(), Timeout, Ct, onLogs: batches.Add);
        var sent = await writer.NextAsync();

        session.ReportLogs(new LogBatch { RequestId = sent.RequestId, Lines = { new LogLine { Text = "ready" } } });
        session.ReportLogs(new LogBatch { RequestId = "someone-else", Lines = { new LogLine { Text = "not ours" } } });
        session.Complete(new CommandResult { RequestId = sent.RequestId });
        await request;
        session.ReportLogs(new LogBatch { RequestId = sent.RequestId, Lines = { new LogLine { Text = "too late" } } });

        Assert.Equal(["ready"], batches.SelectMany(b => b.Lines).Select(l => l.Text));
    }

    [Fact]
    public async Task Close_fails_waiting_requests()
    {
        var request = session.RequestAsync(ListContainers(), Timeout, Ct);
        await writer.NextAsync();

        session.Close("test");

        await Assert.ThrowsAsync<AgentDisconnectedException>(() => request);
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public async Task Request_on_a_closed_session_fails_without_writing()
    {
        session.Close("test");

        await Assert.ThrowsAsync<AgentDisconnectedException>(() => session.RequestAsync(ListContainers(), Timeout, Ct));
        Assert.Equal(0, writer.Count);
    }

    [Fact]
    public async Task Request_times_out_and_a_late_result_is_dropped()
    {
        var request = session.RequestAsync(ListContainers(), TimeSpan.FromMilliseconds(50), Ct);
        var sent = await writer.NextAsync();

        await Assert.ThrowsAsync<AgentTimeoutException>(() => request);
        Assert.Equal(0, session.PendingCount);

        session.Complete(new CommandResult { RequestId = sent.RequestId });
    }

    [Fact]
    public async Task Timed_out_request_is_cancelled_on_the_agent()
    {
        var request = session.RequestAsync(ListContainers(), TimeSpan.FromMilliseconds(50), Ct);
        var sent = await writer.NextAsync();

        await Assert.ThrowsAsync<AgentTimeoutException>(() => request);

        var cancel = await writer.NextAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.CancelRequest, cancel.BodyCase);
        Assert.Equal(sent.RequestId, cancel.CancelRequest.TargetRequestId);
        Assert.NotEqual(sent.RequestId, cancel.RequestId);
    }

    [Fact]
    public async Task Request_the_caller_stops_waiting_for_is_cancelled_on_the_agent()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = session.RequestAsync(ListContainers(), Timeout, caller.Token);
        var sent = await writer.NextAsync();

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var cancel = await writer.NextAsync();
        Assert.Equal(sent.RequestId, cancel.CancelRequest.TargetRequestId);
    }

    [Fact]
    public async Task Completed_request_sends_no_cancellation()
    {
        var request = session.RequestAsync(ListContainers(), Timeout, Ct);
        var sent = await writer.NextAsync();
        session.Complete(new CommandResult { RequestId = sent.RequestId });

        await request;

        Assert.Equal(1, writer.Count);
    }

    [Fact]
    public async Task Request_failed_by_a_closed_session_sends_no_cancellation()
    {
        var request = session.RequestAsync(ListContainers(), Timeout, Ct);
        await writer.NextAsync();

        session.Close("test");

        await Assert.ThrowsAsync<AgentDisconnectedException>(() => request);
        Assert.Equal(1, writer.Count);
    }

    [Fact]
    public async Task Timeout_is_reported_even_when_the_cancellation_cannot_be_sent()
    {
        var timedOut = session.RequestAsync(ListContainers(), TimeSpan.FromMilliseconds(50), Ct);
        await writer.NextAsync();
        writer.FailWrites = true;

        await Assert.ThrowsAsync<AgentTimeoutException>(() => timedOut);
    }

    [Fact]
    public async Task Concurrent_requests_never_write_to_the_stream_in_parallel()
    {
        writer.WriteDelay = TimeSpan.FromMilliseconds(5);
        var requests = Enumerable.Range(0, 20).Select(_ => session.RequestAsync(ListContainers(), Timeout, Ct)).ToList();

        for (var i = 0; i < requests.Count; i++)
        {
            var sent = await writer.NextAsync();
            session.Complete(new CommandResult { RequestId = sent.RequestId });
        }

        await Task.WhenAll(requests);
        Assert.False(writer.Overlapped);
        Assert.Equal(20, requests.Select(r => r.Result.RequestId).Distinct().Count());
    }

    [Fact]
    public void Last_message_time_never_moves_back()
    {
        session.RecordActivity(Now.AddSeconds(10));
        session.RecordActivity(Now.AddSeconds(5));

        Assert.Equal(Now.AddSeconds(10), session.LastMessageAt);
    }

    private static ControllerMessage ListContainers() => new() { ListContainers = new ListContainers() };
}

/// <summary>Stream writer that records messages and detects overlapping writes.</summary>
internal sealed class RecordingWriter : IAsyncStreamWriter<ControllerMessage>
{
    private readonly Channel<ControllerMessage> written = Channel.CreateUnbounded<ControllerMessage>();
    private int active;
    private int count;

    public TimeSpan WriteDelay { get; set; } = TimeSpan.Zero;

    public bool Overlapped { get; private set; }

    public int Count => count;

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(ControllerMessage message) => WriteAsync(message, CancellationToken.None);

    /// <summary>When set, every write fails as a write to a finished call does.</summary>
    public bool FailWrites { get; set; }

    public async Task WriteAsync(ControllerMessage message, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("The call has finished.");
        }

        if (Interlocked.Increment(ref active) > 1)
        {
            Overlapped = true;
        }

        try
        {
            if (WriteDelay > TimeSpan.Zero)
            {
                await Task.Delay(WriteDelay, cancellationToken);
            }

            Interlocked.Increment(ref count);
            written.Writer.TryWrite(message.Clone());
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    public async Task<ControllerMessage> NextAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await written.Reader.ReadAsync(timeout.Token);
    }
}
