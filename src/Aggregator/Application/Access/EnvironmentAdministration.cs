using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// Lets administrators and the environment's own administrators (<see cref="Role.EnvironmentAdmin"/>, ADR-0012) do what
/// concerns a single environment: its policy, roles on it, age keys, registry credentials and the agent.
/// </summary>
public sealed class EnvironmentAdministration(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IRoleAssignmentRepository roleAssignments)
{
    /// <summary>The actor, if they may administer the environment, and the environment.</summary>
    /// <exception cref="NotFoundException">The environment does not exist or the actor does not see it.</exception>
    /// <exception cref="AccessDeniedException">The actor sees it but may not administer it.</exception>
    public async Task<(Actor Actor, DockerEnvironment Environment)> GetAsync(EnvironmentId id, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Environment", id);
        if (actor.IsAdmin)
        {
            return (actor, environment);
        }

        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (!AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", id);
        }

        if (!AccessPolicy.CanPerform(actor, environment, assignments, Permission.AdministerEnvironment))
        {
            throw new AccessDeniedException(actor.Scope.IsReadOnly
                ? "A read-only token cannot administer an environment."
                : $"Only administrators of environment '{environment.Name}' may do this.");
        }

        return (actor, environment);
    }

    /// <summary>Whether the actor may administer the environment; for showing the panel's controls.</summary>
    public static bool CanAdminister(Actor actor, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments) =>
        actor.IsAdmin || AccessPolicy.CanPerform(actor, environment, assignments, Permission.AdministerEnvironment);
}
