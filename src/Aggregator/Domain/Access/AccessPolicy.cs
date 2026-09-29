using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// Decides what a user may see and do.
/// Effective role on a stack = the highest of the roles on its environment and on the stack itself;
/// there are no explicit denials. Every public environment gives each active user an implicit Viewer role.
/// </summary>
public static class AccessPolicy
{
    public static bool CanSee(User user, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments)
    {
        if (!user.IsActive)
        {
            return false;
        }

        return user.IsAdmin
            || !environment.IsPrivate
            || OwnAssignments(user, assignments).Any(a => a.Reveals(environment.Id));
    }

    public static bool CanSee(User user, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments) =>
        CanPerform(user, environment, stack, assignments, Permission.View);

    /// <summary>Returns null when the user has no role on the stack.</summary>
    public static Role? EffectiveRole(User user, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments)
    {
        EnsureSameEnvironment(environment, stack);
        return Highest(user, environment, OwnAssignments(user, assignments).Where(a => a.Covers(stack)));
    }

    /// <summary>Role on the environment as a whole, e.g. for containers that belong to no stack.</summary>
    public static Role? EffectiveRole(User user, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments) =>
        Highest(user, environment, OwnAssignments(user, assignments).Where(a => a.CoversWholeEnvironment(environment.Id)));

    public static bool CanPerform(
        User user, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments, Permission permission)
    {
        EnsureSameEnvironment(environment, stack);
        return IsActiveAdmin(user) || EffectiveRole(user, environment, stack, assignments)?.Grants(permission) == true;
    }

    public static bool CanPerform(
        User user, DockerEnvironment environment, IEnumerable<RoleAssignment> assignments, Permission permission) =>
        IsActiveAdmin(user) || EffectiveRole(user, environment, assignments)?.Grants(permission) == true;

    public static bool CanPerform(
        User user, DockerEnvironment environment, Stack stack, IEnumerable<RoleAssignment> assignments, StackOperation operation) =>
        CanPerform(user, environment, stack, assignments, RolePermissions.RequiredFor(operation));

    private static Role? Highest(User user, DockerEnvironment environment, IEnumerable<RoleAssignment> applicable)
    {
        if (!user.IsActive)
        {
            return null;
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

    private static bool IsActiveAdmin(User user) => user.IsActive && user.IsAdmin;

    private static IEnumerable<RoleAssignment> OwnAssignments(User user, IEnumerable<RoleAssignment> assignments) =>
        assignments.Where(a => a.UserId == user.Id);

    private static void EnsureSameEnvironment(DockerEnvironment environment, Stack stack)
    {
        if (stack.EnvironmentId != environment.Id)
        {
            throw new ArgumentException($"Stack '{stack.Name}' does not belong to environment '{environment.Name}'.", nameof(stack));
        }
    }
}
