using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Notifications;

/// <summary>What became of one notification, as reported to the aggregator.</summary>
/// <param name="Error">Why it was not posted, shown to administrators in the panel; null when delivered.</param>
public sealed record DeliveryResult(string? Error)
{
    public static readonly DeliveryResult Delivered = new(Error: null);

    public bool IsDelivered => Error is null;

    public static DeliveryResult Failed(string error) => new(error);
}

/// <summary>Posts notifications in Discord. Implementations turn Discord's refusals into <see cref="DeliveryResult.Failed"/> instead of throwing.</summary>
public interface INotificationPoster
{
    /// <summary>Completes once Discord can be posted to, i.e. the bot has signed in.</summary>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);

    Task<DeliveryResult> PostAsync(ForestNotification notification, CancellationToken cancellationToken);
}
