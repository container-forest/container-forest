using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Live;

/// <summary>
/// Tells open panels that something changed, so they reload it through the API. An update carries ids only, never
/// names or state: what a user may see is checked by the API when the panel reloads, as for any other request.
/// </summary>
/// <remarks>
/// Best effort: an update that cannot be sent is logged and dropped, it never fails the operation that caused it.
/// The infrastructure publishes the updates itself (after saving changes and after agent commands), so services do
/// not call this port.
/// </remarks>
public interface ILiveUpdates
{
    /// <summary>The list of environments, an agent's status or someone's roles changed; sent to every panel.</summary>
    Task EnvironmentsChangedAsync();

    /// <summary>Containers of the environment may have changed state.</summary>
    Task ContainersChangedAsync(EnvironmentId environmentId);

    Task StackChangedAsync(EnvironmentId environmentId, StackId stackId);

    /// <summary>A deployment was queued, moved on, wrote to its log or finished.</summary>
    Task DeploymentChangedAsync(EnvironmentId environmentId, StackId stackId, DeploymentId deploymentId);
}

/// <summary>Sends nothing; for hosts without panels to tell, such as tools and tests.</summary>
public sealed class NullLiveUpdates : ILiveUpdates
{
    public Task EnvironmentsChangedAsync() => Task.CompletedTask;

    public Task ContainersChangedAsync(EnvironmentId environmentId) => Task.CompletedTask;

    public Task StackChangedAsync(EnvironmentId environmentId, StackId stackId) => Task.CompletedTask;

    public Task DeploymentChangedAsync(EnvironmentId environmentId, StackId stackId, DeploymentId deploymentId) =>
        Task.CompletedTask;
}
