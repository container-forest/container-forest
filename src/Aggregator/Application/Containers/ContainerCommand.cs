using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>
/// Base for container actions: checks that the action makes sense for the container's current state
/// and stack, then sends it to the agent. Subclasses define the allowed states and the agent call.
/// </summary>
public abstract class ContainerCommand(IAgentChannel agents) : IContainerCommand
{
    public abstract string Name { get; }

    public Permission RequiredPermission => Permission.ControlContainers;

    /// <summary>
    /// Whether the action may run on a container of a system stack. Stopping or restarting the aggregator
    /// or the agent from inside would cut the connection the command itself travels over.
    /// </summary>
    protected virtual bool AllowedOnSystemStack => false;

    public async Task ExecuteAsync(ContainerCommandContext context, CancellationToken cancellationToken)
    {
        EnsureAllowedState(context.Container);

        if (context.Stack is { IsSystem: true } stack && !AllowedOnSystemStack)
        {
            throw new DomainConflictException(
                $"Cannot {Name} container '{context.Container.Name}' of system stack '{stack.Name}'.");
        }

        await SendAsync(agents, context.Environment.Id, context.Container.Id, cancellationToken);
    }

    /// <summary>
    /// Whether the action makes sense in this state. <see cref="ContainerState.Unknown"/> is always allowed:
    /// the agent reported a state this version does not know, so Docker decides.
    /// </summary>
    protected abstract bool IsAllowedIn(ContainerState state);

    protected abstract Task SendAsync(
        IAgentChannel agents, EnvironmentId environmentId, string containerId, CancellationToken cancellationToken);

    protected void EnsureAllowedState(ContainerSnapshot container)
    {
        if (container.State != ContainerState.Unknown && !IsAllowedIn(container.State))
        {
            throw new DomainConflictException(
                $"Cannot {Name} container '{container.Name}': it is {container.State.ToString().ToLowerInvariant()}.");
        }
    }
}

public sealed class StartContainerCommand(IAgentChannel agents) : ContainerCommand(agents)
{
    public override string Name => "start";

    // A stopped system container means the aggregator or the agent is down; starting it cannot hurt.
    protected override bool AllowedOnSystemStack => true;

    protected override bool IsAllowedIn(ContainerState state) => state is ContainerState.Created or ContainerState.Exited;

    protected override Task SendAsync(
        IAgentChannel agents, EnvironmentId environmentId, string containerId, CancellationToken cancellationToken) =>
        agents.StartContainerAsync(environmentId, containerId, cancellationToken);
}

/// <summary>Base for actions that stop the running process and wait for it to exit.</summary>
public abstract class StoppingContainerCommand(IAgentChannel agents, TimeSpan stopTimeout) : ContainerCommand(agents)
{
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan MaxStopTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long Docker waits for the process to exit gracefully before killing it.</summary>
    public TimeSpan StopTimeout { get; } = ValidateTimeout(stopTimeout);

    private static TimeSpan ValidateTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout <= MaxStopTimeout
            ? timeout
            : throw new DomainValidationException(
                $"Stop timeout must be between 0 and {MaxStopTimeout.TotalSeconds:0} seconds.");
}

public sealed class StopContainerCommand(IAgentChannel agents, TimeSpan stopTimeout)
    : StoppingContainerCommand(agents, stopTimeout)
{
    public override string Name => "stop";

    protected override bool IsAllowedIn(ContainerState state) =>
        state is ContainerState.Running or ContainerState.Restarting or ContainerState.Paused;

    protected override Task SendAsync(
        IAgentChannel agents, EnvironmentId environmentId, string containerId, CancellationToken cancellationToken) =>
        agents.StopContainerAsync(environmentId, containerId, StopTimeout, cancellationToken);
}

public sealed class RestartContainerCommand(IAgentChannel agents, TimeSpan stopTimeout)
    : StoppingContainerCommand(agents, stopTimeout)
{
    public override string Name => "restart";

    // docker restart also starts a stopped container; only a container being removed or dead cannot restart.
    protected override bool IsAllowedIn(ContainerState state) =>
        state is not (ContainerState.Removing or ContainerState.Dead);

    protected override Task SendAsync(
        IAgentChannel agents, EnvironmentId environmentId, string containerId, CancellationToken cancellationToken) =>
        agents.RestartContainerAsync(environmentId, containerId, StopTimeout, cancellationToken);
}
