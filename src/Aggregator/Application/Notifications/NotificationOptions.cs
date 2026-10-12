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

    /// <summary>
    /// How far apart a container's "die" and the "stop" or "destroy" of a requested stop may be. Docker reports
    /// docker stop, restart and compose down as stop then die, docker rm -f as die then destroy, within milliseconds;
    /// a crash has neither.
    /// </summary>
    public TimeSpan ContainerStopWait { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The shortest interval between two notifications of one kind about one container: a container in a restart
    /// loop dies every few seconds, and one message says it all.
    /// </summary>
    public TimeSpan ContainerRepeatInterval { get; set; } = TimeSpan.FromMinutes(10);

    public bool IsValid() =>
        AgentOfflineDelay > TimeSpan.Zero && ContainerStopWait > TimeSpan.Zero && ContainerRepeatInterval >= TimeSpan.Zero;
}
