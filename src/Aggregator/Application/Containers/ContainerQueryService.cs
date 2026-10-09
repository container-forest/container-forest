using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Containers;

/// <param name="CanControl">
/// Whether the user may start, stop and restart it. Only for hiding buttons: every action is checked again.
/// </param>
/// <param name="IsSystem">
/// A part of container-forest itself: labelled so, or in a system stack. Only starting it is allowed.
/// </param>
public sealed record VisibleContainer(ContainerSnapshot Container, bool CanControl, bool IsSystem);

/// <summary>Containers of an environment, limited to what the current user may see.</summary>
public sealed class ContainerQueryService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IAgentChannel agents)
{
    /// <summary>
    /// A container of a managed stack is visible with a role on that stack or its environment; any other
    /// container needs a role on the environment as a whole.
    /// </summary>
    public async Task<IReadOnlyList<VisibleContainer>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetAsync(cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);

        // Checked before contacting the agent, like container commands.
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        if (environment is null || !AccessPolicy.CanSee(user, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var containers = await agents.ListContainersAsync(environment.Id, cancellationToken);
        var managedStacks = (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken))
            .ToDictionary(s => s.Name.Value, StringComparer.Ordinal);

        var visible = new List<VisibleContainer>();
        foreach (var container in containers)
        {
            var stack = container.StackName is { } name ? managedStacks.GetValueOrDefault(name) : null;
            if (Can(user, environment, stack, assignments, Permission.View))
            {
                visible.Add(new VisibleContainer(
                    container,
                    Can(user, environment, stack, assignments, Permission.ControlContainers),
                    container.IsSystem || stack is { IsSystem: true }));
            }
        }

        return visible;
    }

    private static bool Can(
        Actor user, DockerEnvironment environment, Stack? stack, IReadOnlyList<RoleAssignment> assignments, Permission permission) =>
        stack is not null
            ? AccessPolicy.CanPerform(user, environment, stack, assignments, permission)
            : AccessPolicy.CanPerform(user, environment, assignments, permission);
}
