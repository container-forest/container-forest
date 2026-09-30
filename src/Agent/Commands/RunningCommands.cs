using System.Collections.Concurrent;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Commands in progress by request_id, so a CancelRequest can stop the one it names. Cancelling a command cancels
/// the token its handler got: compose is killed, a Docker API call is aborted, and the command ends with
/// ERROR_CODE_CANCELLED.
/// </summary>
public sealed class RunningCommands
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    public int Count => running.Count;

    /// <param name="sessionToken">Cancelled when the connection ends; the command is cancelled with it.</param>
    /// <returns>The command's registration, or null when a command with the same request_id is already running.</returns>
    public RunningCommand? Begin(string requestId, CancellationToken sessionToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        if (!running.TryAdd(requestId, source))
        {
            source.Dispose();
            return null;
        }

        return new RunningCommand(this, requestId, source);
    }

    /// <returns>False when no command with this request_id is running, e.g. it has already finished.</returns>
    public bool Cancel(string requestId)
    {
        if (!running.TryGetValue(requestId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The command finished between the lookup and the cancellation.
            return false;
        }
    }

    /// <summary>A registered command; disposing it removes the registration once the command has finished.</summary>
    public sealed class RunningCommand(RunningCommands owner, string requestId, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            // Removes only this registration: the pair, not just the key.
            owner.running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestId, source));
            source.Dispose();
        }
    }
}
