using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.SignalR;

namespace ContainerForest.Aggregator.Api.Live;

/// <summary>Adapter from <see cref="ILiveUpdates"/> to <see cref="LiveHub"/>. A failed send is logged, never thrown.</summary>
internal sealed partial class SignalRLiveUpdates(IHubContext<LiveHub, ILiveClient> hub, ILogger<SignalRLiveUpdates> logger)
    : ILiveUpdates
{
    public Task EnvironmentsChangedAsync() =>
        SendAsync(nameof(ILiveClient.EnvironmentsChanged), () => hub.Clients.All.EnvironmentsChanged());

    public Task ContainersChangedAsync(EnvironmentId environmentId) =>
        SendAsync(
            nameof(ILiveClient.ContainersChanged),
            () => hub.Clients.Group(LiveHub.GroupOf(environmentId)).ContainersChanged(environmentId.Value));

    public Task StackChangedAsync(EnvironmentId environmentId, StackId stackId) =>
        SendAsync(
            nameof(ILiveClient.StackChanged),
            () => hub.Clients.Group(LiveHub.GroupOf(environmentId)).StackChanged(environmentId.Value, stackId.Value));

    public Task DeploymentChangedAsync(EnvironmentId environmentId, StackId stackId, DeploymentId deploymentId) =>
        SendAsync(
            nameof(ILiveClient.DeploymentChanged),
            () => hub.Clients.Group(LiveHub.GroupOf(environmentId))
                .DeploymentChanged(environmentId.Value, stackId.Value, deploymentId.Value));

    private async Task SendAsync(string eventName, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            // A panel that misses an update catches up on its next reload; the change itself already happened.
            LogFailed(ex, eventName);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live update {EventName} could not be sent")]
    private partial void LogFailed(Exception exception, string eventName);
}
