using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>An action on one container, such as start or restart.</summary>
public interface IContainerCommand
{
    /// <summary>Short verb for messages and audit, e.g. "restart".</summary>
    string Name { get; }

    Permission RequiredPermission { get; }

    Task ExecuteAsync(ContainerCommandContext context, CancellationToken cancellationToken);
}

/// <summary>Who runs the command and on what.</summary>
/// <param name="Stack">The managed stack the container belongs to (by its compose project label), or null.</param>
public sealed record ContainerCommandContext(
    Actor Actor,
    DockerEnvironment Environment,
    ContainerSnapshot Container,
    Stack? Stack);

public enum ContainerAction
{
    Start,
    Stop,
    Restart,
}
