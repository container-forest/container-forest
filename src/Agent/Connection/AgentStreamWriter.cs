using ContainerForest.Contracts.Agent;
using Grpc.Core;

namespace ContainerForest.Agent.Connection;

/// <summary>
/// The only way to write to the Connect stream. gRPC forbids concurrent writes, while heartbeats,
/// command results and progress are produced by different tasks.
/// </summary>
public sealed class AgentStreamWriter(IAsyncStreamWriter<AgentMessage> stream) : IDisposable
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task WriteAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(message, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public void Dispose() => writeLock.Dispose();
}
