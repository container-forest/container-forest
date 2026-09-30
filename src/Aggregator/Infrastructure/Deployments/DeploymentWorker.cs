using System.Threading.Channels;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>In-memory queue between the request that starts a deployment and the worker that runs it.</summary>
/// <remarks>Lost on restart; the worker then fails what was left, see <see cref="DeploymentRunner.FailInterruptedAsync"/>.</remarks>
internal sealed class DeploymentQueue : IDeploymentQueue
{
    private readonly Channel<DeploymentId> channel = Channel.CreateUnbounded<DeploymentId>();

    public ChannelReader<DeploymentId> Reader => channel.Reader;

    public void Enqueue(DeploymentId id) => channel.Writer.TryWrite(id);
}

/// <summary>Runs queued deployments in the background, each in its own DI scope.</summary>
internal sealed class DeploymentWorker(
    DeploymentQueue queue,
    IServiceScopeFactory scopeFactory,
    DeploymentOptions options,
    ILogger<DeploymentWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the base start: nothing is queued until the leftovers of the previous run are failed.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var failed = await scope.ServiceProvider.GetRequiredService<DeploymentRunner>().FailInterruptedAsync(cancellationToken);
            if (failed > 0)
            {
                logger.LogWarning("Marked {Count} deployment(s) interrupted by the previous shutdown as failed", failed);
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(
            queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrentDeployments, CancellationToken = stoppingToken },
            RunAsync);

    private async ValueTask RunAsync(DeploymentId id, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DeploymentRunner>().RunAsync(id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The pipeline records its own failures; this is e.g. the database being unavailable.
            logger.LogError(ex, "Deployment {DeploymentId} could not be run", id);
        }
    }
}
