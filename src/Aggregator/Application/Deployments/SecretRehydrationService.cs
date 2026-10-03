using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>
/// Restores file secrets after a host reboot. They live on tmpfs, so a rebooted host has dangling links in the stack
/// directories and Docker starts the containers without them. When the agent reports a new boot id, every stack that
/// was deployed with secrets is deployed again at the commit it runs (<see cref="Deployment.QueueRehydrate"/>).
/// </summary>
/// <remarks>
/// Not a user action: it runs on the agent's connection, with no user and no permission check, and only ever repeats
/// a deployment someone already made. A stack that has a deployment in progress is skipped: that deployment sends
/// the secrets itself.
/// </remarks>
public sealed partial class SecretRehydrationService(
    IStackRepository stacks,
    IDeploymentRepository deployments,
    IUnitOfWork unitOfWork,
    IDeploymentQueue queue,
    TimeProvider time,
    ILogger<SecretRehydrationService> logger)
{
    /// <returns>The deployments queued, one per stack.</returns>
    public async Task<IReadOnlyList<Deployment>> RehydrateAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var queued = new List<Deployment>();
        foreach (var stack in await stacks.ListByEnvironmentAsync(environmentId, cancellationToken))
        {
            if (!stack.NeedsRehydration)
            {
                continue;
            }

            if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
            {
                LogSkipped(stack.Name.Value);
                continue;
            }

            var deployment = Deployment.QueueRehydrate(stack.Id, stack.DeployedCommit!, time.GetUtcNow());
            deployments.Add(deployment);
            queued.Add(deployment);
        }

        if (queued.Count == 0)
        {
            return queued;
        }

        // Saved before queueing: the worker loads the deployment from the database.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var deployment in queued)
        {
            queue.Enqueue(deployment.Id);
        }

        return queued;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stack {Stack} has a deployment in progress; it is not rehydrated")]
    private partial void LogSkipped(string stack);
}
