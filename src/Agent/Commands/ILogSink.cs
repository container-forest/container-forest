using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>Sends container log lines of one request to the aggregator.</summary>
/// <remarks>
/// The connection's <see cref="ICommandProgress"/> implements it too: a handler that streams logs
/// casts its progress to this interface, so other handlers keep their signature.
/// </remarks>
public interface ILogSink
{
    /// <summary>Sends the lines as one LogBatch message; the caller keeps it under the gRPC message size limit.</summary>
    Task SendAsync(IReadOnlyCollection<LogLine> lines, CancellationToken cancellationToken);
}
