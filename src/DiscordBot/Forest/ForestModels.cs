namespace ContainerForest.DiscordBot.Forest;

/// <summary>A user of the system linked to a Discord account.</summary>
public sealed record ForestCaller(string UserId, string UserName, bool IsAdmin, bool IsActive);

/// <param name="Id">UUID; pass it back in later calls.</param>
public sealed record ForestEnvironment(string Id, string Name, bool Online);

/// <param name="StackName">Null for a container outside compose.</param>
/// <param name="StatusText">Docker's human-readable status, e.g. "Up 3 minutes (healthy)".</param>
/// <param name="CanControl">The user may start, stop and restart it. Use it to hide buttons, nothing more.</param>
public sealed record ForestContainer(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    bool CanControl);

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
}

/// <param name="Name">The compose project name; unique in the environment, and what the bot passes back.</param>
/// <param name="DeployedCommit">Null when nothing is deployed: before the first successful deployment and after down.</param>
/// <param name="CanRestart">The user may restart it. Use it to hide choices, nothing more.</param>
/// <param name="CanTakeDown">The user may take it down. Use it to hide choices, nothing more.</param>
public sealed record ForestStack(
    string Id,
    string Name,
    bool IsSystem,
    string? DeployedCommit,
    DateTimeOffset? DeployedAt,
    bool CanRestart,
    bool CanTakeDown);

public enum StackAction
{
    Restart,
    Down,
}

/// <summary>What docker compose did with a stack.</summary>
/// <param name="Error">Why compose failed, or null when it succeeded.</param>
/// <param name="Output">The last lines compose printed; they come from Docker, so escape them before showing them.</param>
public sealed record ForestStackResult(string? Error, IReadOnlyList<string> Output)
{
    public bool Succeeded => Error is null;
}
