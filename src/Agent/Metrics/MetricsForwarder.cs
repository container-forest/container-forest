using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Connection;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Metrics;

/// <summary>
/// Sends a <see cref="MetricsSample"/> every Agent:MetricsInterval while a session lasts, with Docker's disk usage
/// every Agent:DiskUsageInterval. A failed reading skips that sample; only a failed write ends the loop, and the
/// session notices the broken stream itself.
/// </summary>
public sealed partial class MetricsForwarder(
    MetricsCollector collector, IOptions<AgentOptions> options, TimeProvider time, ILogger<MetricsForwarder> logger)
{
    private DateTimeOffset? lastDiskUsageAt;
    private Task<DockerDiskUsage?>? pendingDiskUsage;

    /// <summary>Returns when the session ends.</summary>
    public async Task RunAsync(AgentStreamWriter writer, CancellationToken session)
    {
        if (collector.Source is MetricsSource.Disabled or MetricsSource.Unspecified)
        {
            return;
        }

        // A new session may be a restarted aggregator that knows nothing yet.
        lastDiskUsageAt = null;
        try
        {
            using var timer = new PeriodicTimer(options.Value.MetricsInterval, time);
            do
            {
                if (await TryCollectAsync(session) is { } sample)
                {
                    await writer.WriteAsync(new AgentMessage { Metrics = sample }, session);
                }
            }
            while (await timer.WaitForNextTickAsync(session));
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogWriteFailed(ex.Message);
        }
        finally
        {
            if (pendingDiskUsage is { } pending)
            {
                await pending;
                pendingDiskUsage = null;
            }
        }
    }

    private async Task<MetricsSample?> TryCollectAsync(CancellationToken session)
    {
        MetricsSample sample;
        try
        {
            sample = await collector.CollectAsync(session);
        }
        catch (AgentCommandException ex)
        {
            // Docker restarting or unreachable; the host's numbers alone would hide that the containers are unknown.
            LogCollectFailed(ex.Message);
            return null;
        }

        // docker system df can take a minute on a host with many layers: it runs beside the samples, and its result
        // rides along with the first sample after it finishes.
        if (pendingDiskUsage is { IsCompleted: true } finished)
        {
            pendingDiskUsage = null;
            if (await finished is { } usage)
            {
                sample.DiskUsage = usage;
            }
        }

        var now = time.GetUtcNow();
        if (pendingDiskUsage is null && (lastDiskUsageAt is null || now - lastDiskUsageAt >= options.Value.DiskUsageInterval))
        {
            lastDiskUsageAt = now;
            pendingDiskUsage = ReadDiskUsageAsync(session);
        }

        return sample;
    }

    private async Task<DockerDiskUsage?> ReadDiskUsageAsync(CancellationToken session)
    {
        try
        {
            return await collector.GetDiskUsageAsync(session);
        }
        catch (AgentCommandException ex)
        {
            LogDiskUsageFailed(ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resource usage could not be read: {Reason}")]
    private partial void LogCollectFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Docker disk usage could not be read: {Reason}")]
    private partial void LogDiskUsageFailed(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending resource usage stopped: {Reason}")]
    private partial void LogWriteFailed(string reason);
}
