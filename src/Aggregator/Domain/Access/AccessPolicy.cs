using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// Decides what a user may see and do.
/// Effective role on a stack = the highest of the roles on its environment and on the stack itself;
/// there are no explicit denials. Every public environment gives each active user an implicit Viewer role.
/// The actor's <see cref="AccessScope"/> then takes away what its credentials may not reach: environments outside
/// a restricted scope do not exist for it, and a read-only scope allows nothing but viewing.
/// </summary>
public static class AccessPolicy
{
    public static bool CanSee(Actor actor, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments)
    {
        if (!actor.IsActive || !actor.Scope.Reaches(environment.Id))
        {
            return false;
        }

        return actor.User.IsAdmin
            || !environment.IsPrivate
            || OwnAssignments(actor, assignments).Any(a => a.Reveals(environment.Id));
    }

    public static bool CanSee(Actor actor, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments) =>
        CanPerform(actor, environment, stack, assignments, Permission.View);

    /// <summary>
    /// Returns null when the actor has no role on the stack. An administrator has none either, unless their scope is
    /// restricted: then they are an Editor in its environments.
    /// </summary>
    public static Role? EffectiveRole(Actor actor, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments)
    {
        EnsureSameEnvironment(environment, stack);
        return Highest(actor, environment, OwnAssignments(actor, assignments).Where(a => a.Covers(stack)));
    }

    /// <summary>Role on the environment as a whole, e.g. for containers that belong to no stack.</summary>
    public static Role? EffectiveRole(Actor actor, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments) =>
        Highest(actor, environment, OwnAssignments(actor, assignments).Where(a => a.CoversWholeEnvironment(environment.Id)));

    public static bool CanPerform(
        Actor actor, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments, Permission permission)
    {
        EnsureSameEnvironment(environment, stack);
        return actor.Scope.Allows(permission)
            && (actor.IsAdmin || EffectiveRole(actor, environment, stack, assignments)?.Grants(permission) == true);
    }

    public static bool CanPerform(
        Actor actor, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments, Permission permission) =>
        actor.Scope.Allows(permission)
        && (actor.IsAdmin || EffectiveRole(actor, environment, assignments)?.Grants(permission) == true);

    public static bool CanPerform(
        Actor actor, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments, StackOperation operation) =>
        CanPerform(actor, environment, stack, assignments, RolePermissions.RequiredFor(operation));

    /// <summary>
    /// Environments where the actor is an Editor of the environment or of at least one of its stacks: where they may
    /// add stacks or point stacks at repositories. Administrators with a full scope are not special here; callers
    /// check them first. An administrator with a restricted scope is an Editor in each environment of the scope.
    /// </summary>
    public static IReadOnlySet<EnvironmentId> EditorEnvironments(Actor actor, IEnumerable<RoleAssignment> assignments)
    {
        if (!actor.IsActive)
        {
            return new HashSet<EnvironmentId>();
        }

        if (actor.User.IsAdmin && actor.Scope.Environments is { } scoped)
        {
            return scoped.ToHashSet();
        }

        return OwnAssignments(actor, assignments)
            .Where(a => a.Role >= Role.Editor)
            .Select(a => a switch
            {
                EnvironmentRoleAssignment e => e.EnvironmentId,
                StackRoleAssignment s => s.EnvironmentId,
                _ => throw new InvalidOperationException($"Unknown role assignment {a.GetType().Name}."),
            })
            .Where(actor.Scope.Reaches)
            .ToHashSet();
    }

    private static Role? Highest(Actor actor, DockerEnvironment environment, IEnumerable<RoleAssignment> applicable)
    {
        if (!actor.IsActive || !actor.Scope.Reaches(environment.Id))
        {
            return null;
        }

        // An administrator's restricted token: no administrator rights, but everything an Editor may do in its environments.
        if (actor.User.IsAdmin && actor.Scope.IsRestricted)
        {
            return Role.Editor;
        }

        Role? highest = environment.IsPrivate ? null : Role.Viewer;
        foreach (var assignment in applicable)
        {
            if (highest is null || assignment.Role > highest)
            {
                highest = assignment.Role;
            }
        }

        return highest;
    }

    private static IEnumerable<RoleAssignment> OwnAssignments(Actor actor, IEnumerable<RoleAssignment> assignments) =>
        assignments.Where(a => a.UserId == actor.Id);

    private static void EnsureSameEnvironment(DockerEnvironment environment, Stack stack)
    {
        if (stack.EnvironmentId != environment.Id)
        {
            throw new ArgumentException($"Stack '{stack.Name}' does not belong to environment '{environment.Name}'.", nameof(stack));
        }
    }
}
