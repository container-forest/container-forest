using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;
using ContainerState = ContainerForest.Aggregator.Application.Agents.ContainerState;
using ProtoContainerState = ContainerForest.Contracts.Agent.ContainerState;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>Adapter from <see cref="IAgentChannel"/> to the agent protocol.</summary>
internal sealed class GrpcAgentChannel(AgentRegistry registry, IOptions<AgentGatewayOptions> options) : IAgentChannel
{
    public bool IsOnline(EnvironmentId environmentId) => registry.IsOnline(environmentId);

    public async Task<IReadOnlyList<ContainerSnapshot>> ListContainersAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(
            environmentId, new ControllerMessage { ListContainers = new ListContainers() }, options.Value.CommandTimeout, cancellationToken);
        if (result.PayloadCase != CommandResult.PayloadOneofCase.Containers)
        {
            throw new AgentCommandFailedException(environmentId, AgentErrorKind.Internal, "Agent returned no container list.");
        }

        return result.Containers.Items.Select(ToSnapshot).ToList();
    }

    public Task StartContainerAsync(EnvironmentId environmentId, string containerId, CancellationToken cancellationToken) =>
        SendAsync(
            environmentId,
            new ControllerMessage { StartContainer = new StartContainer { ContainerId = containerId } },
            options.Value.CommandTimeout,
            cancellationToken);

    public Task StopContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        SendAsync(
            environmentId,
            new ControllerMessage { StopContainer = new StopContainer { ContainerId = containerId, TimeoutSeconds = Seconds(stopTimeout) } },
            WaitFor(stopTimeout),
            cancellationToken);

    public Task RestartContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        SendAsync(
            environmentId,
            new ControllerMessage { RestartContainer = new RestartContainer { ContainerId = containerId, TimeoutSeconds = Seconds(stopTimeout) } },
            WaitFor(stopTimeout),
            cancellationToken);

    // Docker may wait the whole stop timeout before killing the process; the answer comes after that.
    private TimeSpan WaitFor(TimeSpan stopTimeout) => options.Value.CommandTimeout + stopTimeout;

    private static int Seconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalSeconds);

    private async Task<CommandResult> SendAsync(
        EnvironmentId environmentId, ControllerMessage command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(environmentId, out var session))
        {
            throw new AgentOfflineException(environmentId);
        }

        var result = await session.RequestAsync(command, timeout, cancellationToken);
        if (result.Error is { } error)
        {
            throw new AgentCommandFailedException(environmentId, ToErrorKind(error.Code), error.Message);
        }

        return result;
    }

    private static ContainerSnapshot ToSnapshot(ContainerInfo info) => new(
        info.Id,
        info.Name,
        ToState(info.State),
        info.Image,
        NullIfEmpty(info.StackName),
        NullIfEmpty(info.ServiceName),
        info.StatusText,
        info.CreatedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(info.CreatedUnixMs) : null);

    private static ContainerState ToState(ProtoContainerState state) => state switch
    {
        ProtoContainerState.Created => ContainerState.Created,
        ProtoContainerState.Running => ContainerState.Running,
        ProtoContainerState.Paused => ContainerState.Paused,
        ProtoContainerState.Restarting => ContainerState.Restarting,
        ProtoContainerState.Removing => ContainerState.Removing,
        ProtoContainerState.Exited => ContainerState.Exited,
        ProtoContainerState.Dead => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    private static AgentErrorKind ToErrorKind(ErrorCode code) => code switch
    {
        ErrorCode.InvalidArgument => AgentErrorKind.InvalidArgument,
        ErrorCode.NotFound => AgentErrorKind.NotFound,
        ErrorCode.DockerUnavailable => AgentErrorKind.DockerUnavailable,
        ErrorCode.CommandFailed => AgentErrorKind.CommandFailed,
        ErrorCode.Timeout => AgentErrorKind.Timeout,
        ErrorCode.Cancelled => AgentErrorKind.Cancelled,
        ErrorCode.Busy => AgentErrorKind.Busy,
        ErrorCode.Internal => AgentErrorKind.Internal,
        _ => AgentErrorKind.Unknown,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
