namespace ContainerForest.DiscordBot.Forest;

/// <summary>
/// The aggregator as the notification delivery sees it. Unlike <see cref="IForestClient"/>, nothing here is on
/// behalf of a user: notifications go to channels that administrators chose.
/// </summary>
/// <remarks>Failures are <see cref="ForestException"/> subclasses; cancellation is <see cref="OperationCanceledException"/>.</remarks>
public interface INotificationSource
{
    /// <summary>
    /// Pending notifications, then new ones as they are created, until the stream ends. It ends without an
    /// error when the aggregator closes it, e.g. because another bot instance opened a newer one.
    /// </summary>
    IAsyncEnumerable<ForestNotification> WatchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records whether the notification was posted. Every received notification needs exactly one report;
    /// one left without a report is sent again on the next stream.
    /// </summary>
    /// <param name="error">Why posting failed, shown in the panel; ignored when <paramref name="delivered"/>.</param>
    Task ReportAsync(string notificationId, bool delivered, string? error, CancellationToken cancellationToken);
}
