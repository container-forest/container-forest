using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

/// <summary>What can be done to a single container.</summary>
public enum ContainerAction
{
    Start,
    Stop,
    Restart,

    /// <summary>docker rm of a stopped container; volumes stay.</summary>
    Remove,
}

/// <summary>Compose operations on an unmanaged stack (ADR-0016).</summary>
public enum UnmanagedStackAction
{
    Up,
    Pull,
    Restart,
    Down,
}

public sealed partial class ForestApi
{
    /// <summary>Containers of an environment as its agent reports them now.</summary>
    public async Task<IReadOnlyList<ContainerResponse>> ListContainersAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].Containers.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <param name="container">Id, name or unique id prefix, as with docker.</param>
    /// <param name="timeoutSeconds">For stop and restart; null for the aggregator's default.</param>
    public Task ControlContainerAsync(
        Guid environmentId, string container, ContainerAction action, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var item = Api.Environments[environmentId].Containers[container];
        return action switch
        {
            ContainerAction.Start => item.Start.PostAsync(cancellationToken: cancellationToken),
            ContainerAction.Stop => item.Stop.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            ContainerAction.Restart => item.Restart.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            ContainerAction.Remove => item.Remove.PostAsync(cancellationToken: cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    /// <summary>Compose projects run by hand on the environment's host, as its agent finds them now.</summary>
    public async Task<IReadOnlyList<UnmanagedStackResponse>> ListUnmanagedStacksAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].UnmanagedStacks.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <param name="timeoutSeconds">For restart and down; null for the aggregator's default.</param>
    public async Task<StackCommandResponse> ControlUnmanagedStackAsync(
        Guid environmentId, string stack, UnmanagedStackAction action, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var item = Api.Environments[environmentId].UnmanagedStacks[stack];
        var response = action switch
        {
            UnmanagedStackAction.Up => await item.Up.PostAsync(cancellationToken: cancellationToken),
            UnmanagedStackAction.Pull => await item.Pull.PostAsync(cancellationToken: cancellationToken),
            UnmanagedStackAction.Restart => await item.Restart.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            UnmanagedStackAction.Down => await item.Down.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        return Required(response, "compose output");
    }

    /// <summary>The end of a container's log, oldest first; with <paramref name="since"/> only lines from then on.</summary>
    public async Task<IReadOnlyList<ContainerLogLineResponse>> GetContainerLogsAsync(
        Guid environmentId, string container, int tail, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var response = await Api.Environments[environmentId].Containers[container].Logs.GetAsync(
            c =>
            {
                c.QueryParameters.Tail = tail;
                c.QueryParameters.Since = since;
            },
            cancellationToken);
        return response?.Lines ?? [];
    }
}
