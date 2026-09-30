using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Base for commands on one stack: validates the stack name and runs at most one operation per stack at a time.
/// Whether the operation is allowed (permissions, system stacks) is decided by the aggregator.
/// </summary>
public abstract class StackCommandHandler<TCommand>(StackLocks locks, ILogger logger)
    : AgentCommandHandler<TCommand>(logger)
    where TCommand : class, IMessage<TCommand>
{
    protected sealed override async Task<CommandResult> ExecuteAsync(
        TCommand command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var stackName = StackName.Validate(StackNameOf(command));
        using var lease = locks.TryAcquire(stackName) ?? throw new StackBusyException(stackName);
        await RunAsync(stackName, command, progress, cancellationToken);
        return new CommandResult();
    }

    protected abstract string StackNameOf(TCommand command);

    /// <summary>Runs the operation while the stack is held; <paramref name="stackName"/> is already validated.</summary>
    protected abstract Task RunAsync(string stackName, TCommand command, ICommandProgress progress, CancellationToken cancellationToken);
}

/// <summary>`docker compose restart`: restarts the processes in the stack's existing containers.</summary>
public sealed class RestartStackHandler(
    IStackFileStore store,
    IComposeRunner compose,
    StackLocks locks,
    IOptions<AgentOptions> options,
    ILogger<RestartStackHandler> logger)
    : StackCommandHandler<RestartStack>(locks, logger)
{
    protected override string StackNameOf(RestartStack command) => command.StackName;

    protected override async Task RunAsync(
        string stackName, RestartStack command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var timeout = StopTimeout.FromSeconds(command.TimeoutSeconds);
        if (!store.Exists(stackName))
        {
            throw new StackNotFoundException(stackName);
        }

        // Runs outside the stack directory: without -f, compose must not pick up compose.yaml from the working directory.
        await compose.RunAsync(ComposeInvocation.Restart(stackName, options.Value.StacksRoot, timeout), progress, cancellationToken);
    }
}

/// <summary>`docker compose down`: removes the stack's containers and networks, then its secret files.</summary>
public sealed class DownStackHandler(
    IStackFileStore store,
    IComposeRunner compose,
    StackLocks locks,
    IOptions<AgentOptions> options,
    ILogger<DownStackHandler> logger)
    : StackCommandHandler<DownStack>(locks, logger)
{
    protected override string StackNameOf(DownStack command) => command.StackName;

    protected override async Task RunAsync(
        string stackName, DownStack command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var timeout = StopTimeout.FromSeconds(command.TimeoutSeconds);
        if (!store.Exists(stackName))
        {
            throw new StackNotFoundException(stackName);
        }

        await compose.RunAsync(ComposeInvocation.Down(stackName, options.Value.StacksRoot, timeout), progress, cancellationToken);

        // No container uses them any more; the next deploy writes them again. The stack files stay for a redeploy.
        store.DeleteSecrets(stackName);
    }
}
