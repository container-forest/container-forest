using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.SignalR;

namespace ContainerForest.Aggregator.Api.Live;

/// <summary>
/// What the panel hears on /hubs/live. Every event carries ids only: the panel reloads what changed through the API,
/// which checks the user's rights as for any request. Event types are written by hand in src/Web/src/features/live.
/// </summary>
public interface ILiveClient
{
    /// <summary>Environments, an agent's status or someone's roles changed. Reload environments and call Resubscribe.</summary>
    Task EnvironmentsChanged();

    Task ContainersChanged(Guid environmentId);

    Task StackChanged(Guid environmentId, Guid stackId);

    Task DeploymentChanged(Guid environmentId, Guid stackId, Guid deploymentId);
}

/// <summary>
/// One connection per open panel, signed in with the session cookie. A connection joins a group for each environment
/// its user sees, so ids of other environments' stacks and deployments never reach it. Roles change while panels
/// stay open: <see cref="EnvironmentsChanged"/> goes to everyone, and the panel answers it with <see cref="Resubscribe"/>.
/// </summary>
internal sealed class LiveHub(EnvironmentService environments, ClaimsCurrentUser currentUser) : Hub<ILiveClient>
{
    private const string GroupsKey = "environment-groups";

    public static string GroupOf(EnvironmentId environmentId) => $"environment:{environmentId}";

    public override Task OnConnectedAsync() => SubscribeAsync();

    /// <summary>Joins the groups of the environments the user sees now and leaves the others.</summary>
    public Task Resubscribe() => SubscribeAsync();

    private async Task SubscribeAsync()
    {
        // Unauthenticated (401) and deactivated users (403) throw here, which closes the connection.
        currentUser.Use(Context.User!);
        var visible = (await environments.ListVisibleAsync(Context.ConnectionAborted)).Select(e => GroupOf(e.Id)).ToHashSet();
        var joined = Context.Items.TryGetValue(GroupsKey, out var items) && items is HashSet<string> current ? current : [];

        foreach (var group in joined.Except(visible))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }

        foreach (var group in visible.Except(joined))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }

        Context.Items[GroupsKey] = visible;
    }
}
