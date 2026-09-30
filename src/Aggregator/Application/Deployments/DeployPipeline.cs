using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>One stage of a deployment. Steps share a <see cref="DeployContext"/> and run in registration order.</summary>
public interface IDeployStep
{
    /// <summary>Shown in the deployment log.</summary>
    string Name { get; }

    /// <exception cref="DeploymentFailedException">The deployment cannot continue; the message is shown to the user.</exception>
    Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the steps of a deployment in order and records the outcome: the first failing step ends the run.
/// New stages (SOPS decryption, registry credentials) are added as steps without changing the pipeline.
/// </summary>
public sealed class DeployPipeline(IEnumerable<IDeployStep> steps, IUnitOfWork unitOfWork, ILogger<DeployPipeline> logger)
{
    private readonly IReadOnlyList<IDeployStep> steps = steps.ToList();

    /// <summary>Moves a queued deployment to Succeeded or Failed. Saves after each step so progress is visible.</summary>
    public async Task RunAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var deployment = context.Deployment;
        deployment.Start(context.Now);
        context.Info(
            $"Deploying stack '{context.Stack.Name}' to environment '{context.Environment.Name}' " +
            $"from {context.Repository.Name}@{context.Stack.Branch} (pull: {deployment.PullPolicy}).");
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            foreach (var step in steps)
            {
                context.Info($"Step: {step.Name}");
                await step.ExecuteAsync(context, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            context.Info($"Stack '{context.Stack.Name}' is running commit {deployment.Commit?.Short}.");
            deployment.Succeed(context.Now);
            context.Stack.RecordDeployment(deployment);
        }
        catch (Exception ex)
        {
            var reason = Describe(ex, cancellationToken);
            context.Error(reason);
            deployment.Fail(reason, context.Now);
        }

        // The run is over either way; its outcome is saved even when the aggregator is stopping.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private string Describe(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case DeploymentFailedException or DomainException or AgentException:
                return exception.Message;
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return "The aggregator stopped before the deployment finished.";
            default:
                // Details of unexpected errors stay in the aggregator log: they may reveal internals.
                logger.LogError(exception, "Deployment pipeline failed unexpectedly");
                return "Internal error in the aggregator; see its log.";
        }
    }
}
