using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <summary>Entry point for restart and down of a deployed stack, as the current user is allowed to.</summary>
public sealed class StackCommandService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IDeploymentRepository deployments,
    IAgentChannel agents)
{
    /// <param name="stopTimeout">Per container; <see cref="StackCommand.DefaultStopTimeout"/> when null.</param>
    /// <returns>The compose output; a failed compose run is a result with an error, see <see cref="StackCommand.ExecuteAsync"/>.</returns>
    public async Task<StackCommandResult> ExecuteAsync(
        StackId stackId, StackAction action, TimeSpan? stopTimeout, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        var environment = stack is null ? null : await environments.FindAsync(stack.EnvironmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        // Checked before contacting the agent, and reported as missing so the stack's existence does not leak.
        if (stack is null || environment is null || !AccessPolicy.CanSee(actor, environment, stack, assignments))
        {
            throw new NotFoundException("Stack", stackId);
        }

        var command = Create(action, stopTimeout ?? StackCommand.DefaultStopTimeout);
        if (!AccessPolicy.CanPerform(actor, environment, stack, assignments, command.Operation))
        {
            var role = RolePermissions.MinimumRole(RolePermissions.RequiredFor(command.Operation));
            throw new AccessDeniedException($"Only the {role} role or higher may {command.Name} stack '{stack.Name}'.");
        }

        // The agent answers Busy only while compose runs; a queued or still fetching deployment has not reached it yet.
        if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
        {
            throw new DomainConflictException($"Stack '{stack.Name}' has a deployment in progress.");
        }

        return await command.ExecuteAsync(environment.Id, stack);
    }

    private StackCommand Create(StackAction action, TimeSpan stopTimeout) => action switch
    {
        StackAction.Restart => new RestartStackCommand(agents, stopTimeout),
        StackAction.Down => new DownStackCommand(agents, stopTimeout),
        _ => throw new DomainValidationException($"Unknown stack action {action}."),
    };
}
