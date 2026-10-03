using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

public interface IStackRepository : IRepository<Stack, StackId>
{
    Task<Stack?> FindByNameAsync(EnvironmentId environmentId, StackName name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Stack>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Stacks of all environments that a push to the branch deploys (<see cref="Stack.DeployOnPush"/>).</summary>
    Task<IReadOnlyList<Stack>> ListDeployedOnPushAsync(RepositoryId repositoryId, BranchName branch, CancellationToken cancellationToken);
}
