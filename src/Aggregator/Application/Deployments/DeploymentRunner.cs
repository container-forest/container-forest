using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Policies;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>
/// Runs queued deployments for the background worker. Use one instance (one DI scope) per deployment:
/// a run can take minutes, and its entities must not be shared with other runs.
/// </summary>
public sealed class DeploymentRunner(
    IDeploymentRepository deployments,
    IStackRepository stacks,
    IEnvironmentRepository environments,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
    IComposePolicyRepository policies,
    IUnitOfWork unitOfWork,
    DeployPipeline pipeline,
    TimeProvider time)
{
    public async Task RunAsync(DeploymentId id, CancellationToken cancellationToken)
    {
        var deployment = await deployments.FindAsync(id, cancellationToken);
        if (deployment is not { Status: DeploymentStatus.Queued })
        {
            return;
        }

        var stack = await stacks.GetAsync(deployment.StackId, cancellationToken);
        var environment = await environments.GetAsync(stack.EnvironmentId, cancellationToken);
        var repository = await repositories.GetAsync(stack.RepositoryId, cancellationToken);
        var connection = repository.ConnectionId is { } connectionId ? await connections.GetAsync(connectionId, cancellationToken) : null;

        var policy = await policies.GetAsync(environment.Id, cancellationToken);

        await pipeline.RunAsync(new DeployContext(deployment, stack, environment, repository, time, connection, policy), cancellationToken);
    }

    /// <summary>
    /// Fails deployments left queued or running by a previous run of the aggregator. They are not resumed:
    /// a deployment that starts long after it was requested is worse than an error.
    /// </summary>
    public async Task<int> FailInterruptedAsync(CancellationToken cancellationToken)
    {
        var interrupted = await deployments.ListUnfinishedAsync(cancellationToken);
        foreach (var deployment in interrupted)
        {
            deployment.Fail("The aggregator restarted before the deployment finished.", time.GetUtcNow());
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return interrupted.Count;
    }
}
