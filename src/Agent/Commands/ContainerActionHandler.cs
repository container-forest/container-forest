using System.Text.RegularExpressions;
using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Base for commands on one container: validates the container id before it reaches the Docker API.
/// Whether the action is allowed (state, system stacks, permissions) is decided by the aggregator.
/// </summary>
public abstract partial class ContainerActionHandler<TCommand>(IDockerGateway docker, ILogger logger)
    : AgentCommandHandler<TCommand>(logger)
    where TCommand : class, IMessage<TCommand>
{
    public const int MaxContainerIdLength = 128;

    protected IDockerGateway Docker { get; } = docker;

    protected sealed override async Task<CommandResult> ExecuteAsync(
        TCommand command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var containerId = ValidateContainerId(ContainerIdOf(command));
        await RunAsync(containerId, command, cancellationToken);
        return new CommandResult();
    }

    protected abstract string ContainerIdOf(TCommand command);

    protected abstract Task RunAsync(string containerId, TCommand command, CancellationToken cancellationToken);

    /// <summary>Converts the protocol's timeout_seconds; 0 means "kill at once", as the aggregator allows it.</summary>
    protected static TimeSpan ToStopTimeout(int seconds) =>
        seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidCommandArgumentException("Stop timeout must not be negative.");

    // The id becomes part of a Docker API path, so only characters of container ids and names are allowed.
    // A '/' or ".." could otherwise address a different endpoint.
    private static string ValidateContainerId(string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
        {
            throw new InvalidCommandArgumentException("Container id is required.");
        }

        if (containerId.Length > MaxContainerIdLength || !ContainerIdPattern().IsMatch(containerId))
        {
            throw new InvalidCommandArgumentException("Container id may contain only letters, digits, '_', '.' and '-'.");
        }

        return containerId;
    }

    // \z instead of $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerIdPattern();
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
