using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <summary>
/// An operation on an already deployed stack that the agent runs with docker compose, without sending files:
/// restart or down. Checks that the stack allows it, sends it and collects the compose output.
/// </summary>
public abstract class StackCommand(IAgentChannel agents, TimeSpan stopTimeout)
{
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan MaxStopTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The most output lines a result keeps; the last ones, where compose reports what failed.</summary>
    public const int MaxOutputLines = 1000;

    public abstract StackOperation Operation { get; }

    /// <summary>Short verb for messages, e.g. "restart".</summary>
    public abstract string Name { get; }

    /// <summary>How long Docker waits for each container's process to exit before killing it.</summary>
    public TimeSpan StopTimeout { get; } = ValidateTimeout(stopTimeout);

    /// <summary>
    /// Runs the operation on the stack's agent. A failure of compose itself is a result, not an exception:
    /// the caller needs its output to see what went wrong.
    /// </summary>
    /// <exception cref="DomainConflictException">The stack never allows the operation, e.g. down of a system stack.</exception>
    /// <exception cref="AgentException">The agent is offline, busy with the stack or did not answer.</exception>
    public async Task<StackCommandResult> ExecuteAsync(EnvironmentId environmentId, Stack stack)
    {
        stack.EnsureAllows(Operation);
        if (stack.RequiresUpdateHelper(Operation))
        {
            throw new DomainConflictException(
                $"Cannot {Name} system stack '{stack.Name}': it can only be updated through the helper container, which is not implemented yet.");
        }

        // Compose output arrives on the connection's receive loop while this call waits.
        var output = new ConcurrentQueue<string>();
        try
        {
            // Not the caller's token: once compose has started, a closed browser tab must not cancel it halfway
            // and leave the stack with some containers stopped. The agent call still has its own timeout.
            await SendAsync(agents, environmentId, stack.Name, output.Enqueue, CancellationToken.None);
            return new StackCommandResult(Error: null, Collect(output));
        }
        catch (AgentCommandFailedException failed) when (failed.Kind is AgentErrorKind.CommandFailed or AgentErrorKind.Timeout)
        {
            return new StackCommandResult(failed.Message, Collect(output));
        }
    }

    protected abstract Task SendAsync(
        IAgentChannel agents,
        EnvironmentId environmentId,
        StackName stackName,
        Action<string> onProgress,
        CancellationToken cancellationToken);

    private static IReadOnlyList<string> Collect(ConcurrentQueue<string> output) =>
        output.Count > MaxOutputLines ? output.Skip(output.Count - MaxOutputLines).ToList() : output.ToList();

    private static TimeSpan ValidateTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout <= MaxStopTimeout
            ? timeout
            : throw new DomainValidationException(
                $"Stop timeout must be between 0 and {MaxStopTimeout.TotalSeconds:0} seconds.");
}

/// <summary>docker compose restart: restarts the processes in the stack's existing containers.</summary>
public sealed class RestartStackCommand(IAgentChannel agents, TimeSpan stopTimeout) : StackCommand(agents, stopTimeout)
{
    public override StackOperation Operation => StackOperation.Restart;

    public override string Name => "restart";

    protected override Task SendAsync(
        IAgentChannel agents,
        EnvironmentId environmentId,
        StackName stackName,
        Action<string> onProgress,
        CancellationToken cancellationToken) =>
        agents.RestartStackAsync(environmentId, stackName, StopTimeout, onProgress, cancellationToken);
}

/// <summary>docker compose down: removes the stack's containers and networks; volumes and files stay.</summary>
public sealed class DownStackCommand(IAgentChannel agents, TimeSpan stopTimeout) : StackCommand(agents, stopTimeout)
{
    public override StackOperation Operation => StackOperation.Down;

    public override string Name => "take down";

    protected override Task SendAsync(
        IAgentChannel agents,
        EnvironmentId environmentId,
        StackName stackName,
        Action<string> onProgress,
        CancellationToken cancellationToken) =>
        agents.DownStackAsync(environmentId, stackName, StopTimeout, onProgress, cancellationToken);
}

/// <param name="Error">Why compose failed, or null on success.</param>
/// <param name="Output">Compose output, at most <see cref="StackCommand.MaxOutputLines"/> last lines.</param>
public sealed record StackCommandResult(string? Error, IReadOnlyList<string> Output)
{
    public bool Succeeded => Error is null;
}

public enum StackAction
{
    Restart,
    Down,
}
