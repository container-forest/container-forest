using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>Sends events about the whole system. A notification that cannot be made never breaks what it reports.</summary>
public sealed partial class SystemNotifier(NotificationDispatcher dispatcher, TimeProvider time, ILogger<SystemNotifier> logger)
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
