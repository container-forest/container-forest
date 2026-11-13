using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Commands;

/// <summary>Lists the unmanaged stacks the agent finds on the host (ADR-0016); empty when none are configured.</summary>
public sealed class ListUnmanagedStacksHandler(UnmanagedStackFinder finder, ILogger<ListUnmanagedStacksHandler> logger)
    : AgentCommandHandler<ListUnmanagedStacks>(logger)
{
    protected override async Task<CommandResult> ExecuteAsync(
        ListUnmanagedStacks command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var list = new UnmanagedStackList();
        foreach (var stack in await finder.FindAllAsync(cancellationToken))
        {
            list.Items.Add(new UnmanagedStack
            {
                Name = stack.Name,
                Directory = stack.Directory,
                ConfigFiles = { stack.ConfigFiles },
                System = stack.IsSystem,
                HasContainers = stack.HasContainers,
            });
        }

        return new CommandResult { UnmanagedStacks = list };
    }
}

/// <summary>
/// Pull, up, restart or down of an unmanaged stack. The stack is looked up by name on the host each time: the
/// aggregator never says where it lives. Whether the user may do it, and whether a system stack allows it, is
/// decided by the aggregator.
/// </summary>
public sealed class ControlUnmanagedStackHandler(
    UnmanagedStackFinder finder,
    IComposeRunner compose,
    RegistryConfigWriter registryConfigs,
    StackLocks locks,
    IOptions<AgentOptions> options,
    ILogger<ControlUnmanagedStackHandler> logger)
    : StackCommandHandler<ControlUnmanagedStack>(locks, logger)
{
    protected override string StackNameOf(ControlUnmanagedStack command) => command.StackName;

    protected override async Task RunAsync(
        string stackName, ControlUnmanagedStack command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        if (command.Operation == UnmanagedStackOperation.Unspecified || !Enum.IsDefined(command.Operation))
        {
            throw new InvalidCommandArgumentException("The operation must be up, pull, restart or down.");
        }

        var timeout = StopTimeout.FromSeconds(command.TimeoutSeconds);
        RegistryConfigWriter.Validate(command.RegistryAuths);
        var stack = await finder.FindAsync(stackName, cancellationToken) ?? throw new UnmanagedStackNotFoundException(stackName);

        var invocation = command.Operation switch
        {
            UnmanagedStackOperation.Up => ComposeInvocation.UnmanagedUp(stack),
            UnmanagedStackOperation.Pull => ComposeInvocation.UnmanagedPull(stack),

            // As for managed stacks, without -f outside the project directory: compose works from the containers'
            // labels and needs neither the compose files, which may be gone, nor their variables.
            UnmanagedStackOperation.Restart => ComposeInvocation.Restart(stackName, options.Value.StacksRoot, timeout),
            _ => ComposeInvocation.Down(stackName, options.Value.StacksRoot, timeout),
        };

        // Restart and down run in the stacks directory, which a host with no managed stack may not have yet.
        if (command.Operation is UnmanagedStackOperation.Restart or UnmanagedStackOperation.Down)
        {
            Directory.CreateDirectory(options.Value.StacksRoot);
        }

        using var registryConfig = await registryConfigs.WriteAsync(command.RegistryAuths, cancellationToken);
        if (registryConfig is not null)
        {
            invocation = invocation.WithEnvironment(RegistryConfigWriter.DockerConfigVariable, registryConfig.Directory);
        }

        await compose.RunAsync(invocation, progress, cancellationToken);
    }
}
