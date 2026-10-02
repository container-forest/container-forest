using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Sends the end of a container's log as LogBatch messages, then a successful result.
/// Following the log is not supported yet: the aggregator reads logs once and asks again with since.
/// </summary>
public sealed class StreamContainerLogsHandler(
    IDockerGateway docker, TimeProvider time, ILogger<StreamContainerLogsHandler> logger)
    : AgentCommandHandler<StreamContainerLogs>(logger)
{
    protected override async Task<CommandResult> ExecuteAsync(
        StreamContainerLogs command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var containerId = ContainerIds.Validate(command.ContainerId);
        if (command.TailLines < 0)
        {
            throw new InvalidCommandArgumentException("tail_lines must not be negative.");
        }

        if (command.SinceUnixMs < 0)
        {
            throw new InvalidCommandArgumentException("since_unix_ms must not be negative.");
        }

        if (command.Follow)
        {
            throw new UnsupportedOptionException("following container logs");
        }

        if (progress is not ILogSink sink)
        {
            throw new InvalidOperationException($"The progress of {Handles} must also be an {nameof(ILogSink)}.");
        }

        var batcher = new LogBatcher(sink, time);
        var since = command.SinceUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(command.SinceUnixMs) : (DateTimeOffset?)null;
        await docker.ReadContainerLogsAsync(containerId, command.TailLines, since, batcher.AddAsync, cancellationToken);
        await batcher.FlushAsync(cancellationToken);

        // The lines themselves stay out of the agent log: applications may print secrets.
        logger.LogDebug("Sent {LineCount} log lines of container {ContainerId}", batcher.SentLines, containerId);
        return new CommandResult();
    }
}
