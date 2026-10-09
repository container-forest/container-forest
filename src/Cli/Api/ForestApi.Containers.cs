using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

/// <summary>What can be done to a single container.</summary>
public enum ContainerAction
{
    Start,
    Stop,
    Restart,
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
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
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
