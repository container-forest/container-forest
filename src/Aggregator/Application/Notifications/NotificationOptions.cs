namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>Configuration section "Notifications" (env: CF_NOTIFICATIONS__...).</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// How long an agent may stay disconnected before "agent offline" is notified. Agents reconnect on their own
    /// within seconds (a network blip, an agent or aggregator update), and those are not worth a message.
    /// </summary>
    public TimeSpan AgentOfflineDelay { get; set; } = TimeSpan.FromMinutes(2);

    public bool IsValid() => AgentOfflineDelay > TimeSpan.Zero;
}
