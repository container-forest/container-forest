using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>Sends events about the whole system. A notification that cannot be made never breaks what it reports.</summary>
public interface ISystemNotifier
{
    /// <param name="summary">One line of plain text; no secret values.</param>
    Task NotifyAsync(NotificationEventKinds kind, string summary);
}

/// <summary><see cref="ISystemNotifier"/> over the rules and the bot; a failure is only logged.</summary>
public sealed partial class SystemNotifier(NotificationDispatcher dispatcher, TimeProvider time, ILogger<SystemNotifier> logger) : ISystemNotifier
{
    public async Task NotifyAsync(NotificationEventKinds kind, string summary)
    {
        try
        {
            await dispatcher.DispatchAsync(new NotificationEvent(kind, null, null, time.GetUtcNow()), summary, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogFailed(exception, kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create a {Kind} notification")]
    private partial void LogFailed(Exception exception, NotificationEventKinds kind);
}
