using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>Role on an environment or a stack. Values are ordered: a higher role includes everything a lower one allows.</summary>
public enum Role
{
    Viewer = 1,
    Operator = 2,
    Editor = 3,

    /// <summary>
    /// Everything an Editor may do, plus administering the environment itself: its compose policy, roles of other
    /// users on it and its stacks, age keys, registry credentials and the agent (ADR-0012). Given only on a whole
    /// environment, by an administrator, to people who have root on its host anyway.
    /// </summary>
    EnvironmentAdmin = 4,
}

public enum Permission
{
    /// <summary>See stacks, containers and their status.</summary>
    View,

    /// <summary>Read container logs. Logs can contain secrets that applications print themselves.</summary>
    ViewLogs,

    /// <summary>Start, stop and restart containers; restart a stack.</summary>
    ControlContainers,

    /// <summary>Exec into a container or attach to its console.</summary>
    Exec,

    /// <summary>Edit a stack, deploy it, take it down or remove it. Effectively root on the host.</summary>
    Deploy,

    /// <summary>Administer the environment: compose policy, roles on it, age keys, registry credentials, the agent.</summary>
    AdministerEnvironment,
}

public static class RolePermissions
{
    public static Role MinimumRole(Permission permission) => permission switch
    {
        Permission.View or Permission.ViewLogs => Role.Viewer,
        Permission.ControlContainers => Role.Operator,
        Permission.Exec or Permission.Deploy => Role.Editor,
        Permission.AdministerEnvironment => Role.EnvironmentAdmin,
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null),
    };

    public static bool Grants(this Role role, Permission permission) => role >= MinimumRole(permission);

    public static Permission RequiredFor(StackOperation operation) => operation switch
    {
        StackOperation.Restart => Permission.ControlContainers,
        StackOperation.Redeploy or StackOperation.RedeployWithPull or StackOperation.Down or StackOperation.Remove
            => Permission.Deploy,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };
}
