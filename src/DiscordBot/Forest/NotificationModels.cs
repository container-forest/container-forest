namespace ContainerForest.DiscordBot.Forest;

/// <summary>A notification the aggregator wants posted in a Discord channel.</summary>
/// <param name="Summary">One line of plain text, the same the panel shows in the history.</param>
/// <param name="Deployment">Set for deployment kinds.</param>
/// <param name="Container">Set for container kinds.</param>
public sealed record ForestNotification(
    string Id,
    ulong DiscordChannelId,
    NotificationKind Kind,
    NotificationSeverity Severity,
    DateTimeOffset OccurredAt,
    string EnvironmentId,
    string EnvironmentName,
    string Summary,
    DeploymentEvent? Deployment = null,
    ContainerEvent? Container = null);

/// <param name="CommitSha">Null when the deployment failed before the commit was known.</param>
/// <param name="Error">The failure reason; null when the deployment succeeded.</param>
public sealed record DeploymentEvent(
    string DeploymentId, string StackId, string StackName, string? CommitSha, bool PulledImages, string? Error);

/// <param name="StackName">Null for a container outside compose.</param>
/// <param name="ExitCode">Set when the container died.</param>
/// <param name="IsSystem">The container may only be started (a part of container-forest itself): no Restart button.</param>
public sealed record ContainerEvent(
    string ContainerId, string ContainerName, string? StackName, string? ServiceName, int? ExitCode, bool IsSystem = false);

/// <summary>What happened; <see cref="Unknown"/> for a kind this bot does not know yet.</summary>
public enum NotificationKind
{
    Unknown,
    DeploymentSucceeded,
    DeploymentFailed,
    ContainerDied,
    ContainerUnhealthy,
    AgentOffline,
    AgentOnline,
    ContainerHealthy,
    BackupFailed,
    SettingsChanged,
    AccountSecurityChanged,
}

public enum NotificationSeverity
{
    Unknown,
    Info,
    Warning,
    Error,
}
