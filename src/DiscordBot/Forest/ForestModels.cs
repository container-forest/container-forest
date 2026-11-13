namespace ContainerForest.DiscordBot.Forest;

/// <summary>A user of the system linked to a Discord account.</summary>
public sealed record ForestCaller(string UserId, string UserName, bool IsAdmin, bool IsActive);

/// <param name="Id">UUID; pass it back in later calls.</param>
public sealed record ForestEnvironment(string Id, string Name, bool Online);

/// <param name="StackName">Null for a container outside compose.</param>
/// <param name="StatusText">Docker's human-readable status, e.g. "Up 3 minutes (healthy)".</param>
/// <param name="CanControl">The user may start, stop and restart it. Use it to hide buttons, nothing more.</param>
/// <param name="CanRemove">The user may remove it once it is stopped. Use it to hide choices, nothing more.</param>
public sealed record ForestContainer(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    bool CanControl,
    bool CanRemove = false);

/// <summary>Docker's container states; <see cref="Unknown"/> for a value this bot does not know.</summary>
public enum ContainerState
{
    Unknown,
    Created,
    Running,
    Paused,
    Restarting,
    Removing,
    Exited,
    Dead,
}

public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Remove,
}

/// <param name="Name">The compose project name; unique in the environment, and what the bot passes back.</param>
/// <param name="DeployedCommit">Null when nothing is deployed: before the first successful deployment and after down.</param>
/// <param name="CanRestart">The user may restart it. Use it to hide choices, nothing more.</param>
/// <param name="CanTakeDown">The user may take it down. Use it to hide choices, nothing more.</param>
/// <param name="CanDeploy">The user may redeploy it. Use it to hide choices, nothing more.</param>
/// <param name="IsUnmanaged">A compose project run by hand on the host (ADR-0016): no id, no deployments; up and pull instead.</param>
/// <param name="CanUp">Unmanaged only: the user may bring it up.</param>
/// <param name="CanPull">Unmanaged only: the user may pull its images.</param>
public sealed record ForestStack(
    string Id,
    string Name,
    bool IsSystem,
    string? DeployedCommit,
    DateTimeOffset? DeployedAt,
    bool CanRestart,
    bool CanTakeDown,
    bool CanDeploy = false,
    bool IsUnmanaged = false,
    bool CanUp = false,
    bool CanPull = false);

public enum StackAction
{
    Restart,
    Down,

    /// <summary>Unmanaged stacks only: docker compose up -d.</summary>
    Up,

    /// <summary>Unmanaged stacks only: docker compose pull.</summary>
    Pull,
}

/// <summary>What docker compose did with a stack.</summary>
/// <param name="Error">Why compose failed, or null when it succeeded.</param>
/// <param name="Output">The last lines compose printed; they come from Docker, so escape them before showing them.</param>
public sealed record ForestStackResult(string? Error, IReadOnlyList<string> Output)
{
    public bool Succeeded => Error is null;
}

/// <summary>One deployment of a stack, as far as it has got.</summary>
/// <param name="Commit">Null until the deployment has fetched it.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="FinishedAt">Null while it is queued or running.</param>
public sealed record ForestDeployment(
    DeploymentStatus Status, string? Commit, string? Error, DateTimeOffset QueuedAt, DateTimeOffset? FinishedAt)
{
    public bool IsFinished => Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled;
}

/// <summary>A deployment's state; <see cref="Unknown"/> for a value this bot does not know.</summary>
public enum DeploymentStatus
{
    Unknown,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One line of a container's log.</summary>
/// <param name="At">Null for a line Docker gave no time.</param>
/// <param name="IsStderr">The container wrote it to stderr.</param>
public sealed record ForestLogLine(DateTimeOffset? At, bool IsStderr, string Text);
