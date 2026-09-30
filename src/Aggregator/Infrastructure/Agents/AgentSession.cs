using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>
/// One live Connect call of an agent. Adds request/response on top of the bidirectional stream:
/// each command gets a request_id, and the matching CommandResult completes the waiting caller.
/// </summary>
public sealed class AgentSession
{
    private readonly IAsyncStreamWriter<ControllerMessage> writer;
    private readonly ILogger logger;

    // gRPC forbids concurrent writes to one stream.
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> pending = new();
    private readonly CancellationTokenSource closed = new();
    private int closing;

    public AgentSession(
        EnvironmentId environmentId, IAsyncStreamWriter<ControllerMessage> writer, DateTimeOffset connectedAt, ILogger logger)
    {
        EnvironmentId = environmentId;
        this.writer = writer;
        this.logger = logger;
        ConnectedAt = connectedAt;
        LastMessageAt = connectedAt;
    }

    public EnvironmentId EnvironmentId { get; }

    public Guid SessionId { get; } = Guid.NewGuid();

    public DateTimeOffset ConnectedAt { get; }

    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>Cancelled when the session is closed, e.g. replaced by a newer connection of the same agent.</summary>
    public CancellationToken Closed => closed.Token;

    public bool IsClosed => Volatile.Read(ref closing) != 0;

    public int PendingCount => pending.Count;

    /// <summary>Sends a command and waits for its result.</summary>
    /// <param name="onProgress">Receives the command's output lines (CommandProgress) until the result arrives.</param>
    /// <exception cref="AgentDisconnectedException">The session closed before the result arrived.</exception>
    /// <exception cref="AgentTimeoutException">No result within <paramref name="timeout"/>.</exception>
    public async Task<CommandResult> RequestAsync(
        ControllerMessage command, TimeSpan timeout, CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        command.RequestId = requestId;

        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[requestId] = new PendingRequest(completion, onProgress);
        try
        {
            // Checked after registering: Close marks the session closed before it fails pending requests,
            // so a request registered after that sweep is caught here.
            if (IsClosed)
            {
                throw new AgentDisconnectedException(EnvironmentId);
            }

            await WriteAsync(command, cancellationToken);
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new AgentTimeoutException(EnvironmentId, timeout);
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    public async Task WriteAsync(ControllerMessage message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
        try
        {
            await writeLock.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (IsClosed && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentDisconnectedException(EnvironmentId);
        }

        try
        {
            await writer.WriteAsync(message, linked.Token);
        }
        catch (Exception ex) when (IsClosed && !cancellationToken.IsCancellationRequested && ex is not AgentException)
        {
            // Writing to a call that has already ended throws; for the caller it means the agent is gone.
            throw new AgentDisconnectedException(EnvironmentId);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Called for every message from the agent.</summary>
    public void RecordActivity(DateTimeOffset now)
    {
        if (now > LastMessageAt)
        {
            LastMessageAt = now;
        }
    }

    /// <summary>Passes an output line to the request it belongs to. Lines of finished requests are dropped.</summary>
    public void ReportProgress(CommandProgress progress)
    {
        if (pending.TryGetValue(progress.RequestId, out var request) && request.OnProgress is { } onProgress)
        {
            onProgress(progress.Line);
        }
    }

    /// <summary>Completes the request the result belongs to. Late results of timed-out requests are dropped.</summary>
    public void Complete(CommandResult result)
    {
        if (pending.TryRemove(result.RequestId, out var request))
        {
            request.Completion.TrySetResult(result);
        }
        else
        {
            logger.LogDebug(
                "Dropping result for unknown or expired request {RequestId} from environment {EnvironmentId}",
                result.RequestId, EnvironmentId);
        }
    }

    /// <summary>Closes the session: every waiting request fails with <see cref="AgentDisconnectedException"/>.</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0)
        {
            return;
        }

        logger.LogInformation(
            "Agent session {SessionId} of environment {EnvironmentId} closed: {Reason}", SessionId, EnvironmentId, reason);
        closed.Cancel();

        foreach (var (requestId, request) in pending)
        {
            if (pending.TryRemove(requestId, out _))
            {
                request.Completion.TrySetException(new AgentDisconnectedException(EnvironmentId));
            }
        }
    }

    private sealed record PendingRequest(TaskCompletionSource<CommandResult> Completion, Action<string>? OnProgress);
}
