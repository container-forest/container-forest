using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>A deployment run that has ended, as observers see it. The entities are the run's own; observers only read them.</summary>
/// <param name="Deployment">Succeeded or Failed, with the outcome already saved.</param>
public sealed record DeploymentOutcome(Deployment Deployment, Stack Stack, DockerEnvironment Environment);

/// <summary>
/// Hears about every deployment run once it has ended, e.g. to send a notification. Registered in DI;
/// <see cref="DeployPipeline"/> calls each one after saving the outcome, in the run's DI scope.
/// </summary>
/// <remarks>
/// The deployment is over by then: an observer logs its own failures instead of throwing, so it can neither
/// change the recorded outcome nor stop the next observer.
/// </remarks>
public interface IDeploymentObserver
{
    Task DeploymentFinishedAsync(DeploymentOutcome outcome, CancellationToken cancellationToken);
}
