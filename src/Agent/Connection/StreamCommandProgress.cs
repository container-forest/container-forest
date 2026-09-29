using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Connection;

/// <summary>Sends progress lines of one request as CommandProgress messages.</summary>
public sealed class StreamCommandProgress(string requestId, AgentStreamWriter writer) : ICommandProgress
{
    public Task ReportAsync(string line, CancellationToken cancellationToken) =>
        writer.WriteAsync(new AgentMessage { Progress = new CommandProgress { RequestId = requestId, Line = line } }, cancellationToken);
}
