using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <summary>Entry point for restart and down of a deployed stack from the panel and the bot, as the current user is allowed to.</summary>
public sealed class StackCommandService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IDeploymentRepository deployments,
    IUnitOfWork unitOfWork,
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

        return await RunAsync(actor, environment, stack, assignments, action, stopTimeout, cancellationToken);
    }

    /// <summary>The same by the stack's name in the environment, as the bot names it.</summary>
    public async Task<StackCommandResult> ExecuteAsync(
        EnvironmentId environmentId, string stackName, StackAction action, TimeSpan? stopTimeout, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var stack = environment is not null && StackName.TryParse(stackName.Trim(), out var name)
            ? await stacks.FindByNameAsync(environment.Id, name, cancellationToken)
            : null;
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        // One answer for a missing environment, a missing stack and an invisible one.
        if (stack is null || environment is null || !AccessPolicy.CanSee(actor, environment, stack, assignments))
        {
            throw new NotFoundException("Stack", $"'{stackName.Trim()}'");
        }

        return await RunAsync(actor, environment, stack, assignments, action, stopTimeout, cancellationToken);
    }

    private async Task<StackCommandResult> RunAsync(
        Actor actor,
        DockerEnvironment environment,
        Stack stack,
        IReadOnlyList<RoleAssignment> assignments,
        StackAction action,
        TimeSpan? stopTimeout,
        CancellationToken cancellationToken)
    {
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

        var result = await command.ExecuteAsync(environment.Id, stack);

        // After a failed down some containers may still run the deployed commit, so it is kept.
        if (result.Succeeded && command.Operation == StackOperation.Down)
        {
            stack.RecordTakenDown();

            // Not the caller's token, as for the agent call: compose has already taken the stack down.
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }

        return result;
    }

    private StackCommand Create(StackAction action, TimeSpan stopTimeout) => action switch
    {
        StackAction.Restart => new RestartStackCommand(agents, stopTimeout),
        StackAction.Down => new DownStackCommand(agents, stopTimeout),
        _ => throw new DomainValidationException($"Unknown stack action {action}."),
    };
}
