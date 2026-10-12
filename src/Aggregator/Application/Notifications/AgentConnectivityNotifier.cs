using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>
/// Turns an agent going offline or coming back into a notification. When to call it is decided by
/// <see cref="AgentConnectivityWatch"/>, which waits out short reconnects.
/// </summary>
public sealed class AgentConnectivityNotifier(
    NotificationDispatcher dispatcher, IEnvironmentRepository environments, ILogger<AgentConnectivityNotifier> logger)
{
    /// <param name="kind"><see cref="NotificationEventKinds.AgentOffline"/> or <see cref="NotificationEventKinds.AgentOnline"/>.</param>
    /// <param name="occurredAt">The agent's last message for offline, the new connection for online.</param>
    /// <returns>The notifications created; empty when no rule matches or the environment no longer exists.</returns>
    public async Task<IReadOnlyList<Notification>> NotifyAsync(
        EnvironmentId environmentId, NotificationEventKinds kind, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        if (kind is not (NotificationEventKinds.AgentOffline or NotificationEventKinds.AgentOnline))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only agent events are notified here.");
        }

        // A deleted environment disconnects its agent; that is not news for anyone.
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        if (environment is null)
        {
            return [];
        }

        var notificationEvent = new NotificationEvent(kind, environmentId, StackId: null, occurredAt);
        var created = await dispatcher.DispatchAsync(notificationEvent, Summarize(kind, environment.Name), cancellationToken);
        if (created.Count > 0)
        {
            logger.LogInformation(
                "{Kind} of environment {EnvironmentId} queued {Count} notification(s)", kind, environmentId, created.Count);
        }

        return created;
    }

    /// <summary>The line the history screen and the Discord message show.</summary>
    public static string Summarize(NotificationEventKinds kind, string environmentName) => kind switch
    {
        NotificationEventKinds.AgentOffline => $"The agent of '{environmentName}' went offline and has not reconnected.",
        NotificationEventKinds.AgentOnline => $"The agent of '{environmentName}' is back online.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only agent events are summarized here."),
    };
}
