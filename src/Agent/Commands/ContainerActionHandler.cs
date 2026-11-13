using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Base for commands on one container: validates the container id before it reaches the Docker API.
/// Whether the action is allowed (state, system stacks, permissions) is decided by the aggregator.
/// </summary>
public abstract class ContainerActionHandler<TCommand>(IDockerGateway docker, ILogger logger)
    : AgentCommandHandler<TCommand>(logger)
    where TCommand : class, IMessage<TCommand>
{
    protected IDockerGateway Docker { get; } = docker;

    protected sealed override async Task<CommandResult> ExecuteAsync(
        TCommand command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var containerId = ContainerIds.Validate(ContainerIdOf(command));
        await RunAsync(containerId, command, cancellationToken);
        return new CommandResult();
    }

    protected abstract string ContainerIdOf(TCommand command);

    protected abstract Task RunAsync(string containerId, TCommand command, CancellationToken cancellationToken);

    protected static TimeSpan ToStopTimeout(int seconds) => StopTimeout.FromSeconds(seconds);
}

public sealed class StartContainerHandler(IDockerGateway docker, ILogger<StartContainerHandler> logger)
    : ContainerActionHandler<StartContainer>(docker, logger)
{
    protected override string ContainerIdOf(StartContainer command) => command.ContainerId;

    protected override Task RunAsync(string containerId, StartContainer command, CancellationToken cancellationToken) =>
        Docker.StartContainerAsync(containerId, cancellationToken);
}

public sealed class StopContainerHandler(IDockerGateway docker, ILogger<StopContainerHandler> logger)
    : ContainerActionHandler<StopContainer>(docker, logger)
{
    protected override string ContainerIdOf(StopContainer command) => command.ContainerId;

    protected override Task RunAsync(string containerId, StopContainer command, CancellationToken cancellationToken) =>
        Docker.StopContainerAsync(containerId, ToStopTimeout(command.TimeoutSeconds), cancellationToken);
}

public sealed class RestartContainerHandler(IDockerGateway docker, ILogger<RestartContainerHandler> logger)
    : ContainerActionHandler<RestartContainer>(docker, logger)
{
    protected override string ContainerIdOf(RestartContainer command) => command.ContainerId;

    protected override Task RunAsync(string containerId, RestartContainer command, CancellationToken cancellationToken) =>
        Docker.RestartContainerAsync(containerId, ToStopTimeout(command.TimeoutSeconds), cancellationToken);
}

public sealed class RemoveContainerHandler(IDockerGateway docker, ILogger<RemoveContainerHandler> logger)
    : ContainerActionHandler<RemoveContainer>(docker, logger)
{
    protected override string ContainerIdOf(RemoveContainer command) => command.ContainerId;

    protected override Task RunAsync(string containerId, RemoveContainer command, CancellationToken cancellationToken) =>
        Docker.RemoveContainerAsync(containerId, cancellationToken);
}
