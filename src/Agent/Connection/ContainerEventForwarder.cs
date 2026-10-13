using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Connection;

/// <summary>
/// Sends the aggregator a <see cref="ContainerEvent"/> for each relevant Docker container event while a session lasts,
/// so open panels see containers change state, including changes made outside container-forest (a crash, a manual
/// docker command). Events during a disconnect are lost; panels reload everything after the agent reconnects.
/// </summary>
public sealed partial class ContainerEventForwarder(IDockerGateway docker, TimeProvider time, ILogger<ContainerEventForwarder> logger)
{
    /// <summary>Wait before following events again after Docker went away or the stream failed.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Returns when the session ends.</summary>
    public async Task RunAsync(AgentStreamWriter writer, CancellationToken session)
    {
        while (!session.IsCancellationRequested)
        {
            try
            {
                await docker.WatchContainerEventsAsync((e, ct) => ForwardAsync(e, writer, ct), session);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Docker restarting or unreachable: commands report that themselves; events resume when it is back.
                LogInterrupted(ex.Message, RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, time, session);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ForwardAsync(DockerContainerEvent dockerEvent, AgentStreamWriter writer, CancellationToken cancellationToken)
    {
        if (!ContainerEventFilter.IsRelevant(dockerEvent.Action))
        {
            return;
        }

        var message = new ContainerEvent
        {
            ContainerId = dockerEvent.ContainerId,
            Action = dockerEvent.Action,
            TimestampUnixMs = dockerEvent.At.ToUnixTimeMilliseconds(),
        };
        if (dockerEvent.ExitCode is { } exitCode)
        {
            message.ExitCode = exitCode;
        }

        if (!ContainerEventFilter.IsRemoval(dockerEvent.Action) && await TryFindAsync(dockerEvent.ContainerId, cancellationToken) is { } container)
        {
            message.Container = container;
        }

        await writer.WriteAsync(new AgentMessage { ContainerEvent = message }, cancellationToken);
    }

    // The event is worth sending without the state too: the aggregator only needs to know that something changed.
    private async Task<ContainerInfo?> TryFindAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            return await docker.FindContainerAsync(containerId, cancellationToken);
        }
        catch (AgentCommandException ex)
        {
            LogLookupFailed(containerId, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Following Docker events stopped ({Reason}); retrying in {Seconds} s")]
    private partial void LogInterrupted(string reason, double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Container {ContainerId} could not be looked up for its event: {Reason}")]
    private partial void LogLookupFailed(string containerId, string reason);
}
