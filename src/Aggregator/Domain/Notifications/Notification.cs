using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Notifications;

public enum NotificationStatus
{
    /// <summary>Waiting for delivery, e.g. while the bot is disconnected.</summary>
    Pending,
    Delivered,
    Failed,

    /// <summary>Not delivered within <see cref="Notification.MaxPendingAge"/>; news that old is noise.</summary>
    Expired,
}

/// <summary>
/// One notification for one channel, kept as history: Pending → Delivered | Failed | Expired.
/// Finished notifications never change.
/// </summary>
public sealed class Notification : Entity<NotificationId>
{
    public const int MaxSummaryLength = 500;
    public const int MaxErrorLength = 500;

    /// <summary>A notification still pending after this long is expired instead of delivered late.</summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(1);

    private Notification(
        NotificationId id,
        NotificationChannelId channelId,
        NotificationRuleId ruleId,
        NotificationEvent notificationEvent,
        string summary,
        DateTimeOffset createdAt)
        : base(id)
    {
        ChannelId = channelId;
        RuleId = ruleId;
        Kind = notificationEvent.Kind;
        Severity = notificationEvent.Severity;
        EnvironmentId = notificationEvent.EnvironmentId;
        StackId = notificationEvent.StackId;
        OccurredAt = notificationEvent.OccurredAt;
        Summary = summary;
        CreatedAt = createdAt;
        Status = NotificationStatus.Pending;
    }

    // For EF Core.
    private Notification()
    {
        Summary = null!;
    }

    public NotificationChannelId ChannelId { get; private init; }

    /// <summary>The rule that matched; kept for the history screen even if the rule is changed later.</summary>
    public NotificationRuleId RuleId { get; private init; }

    public NotificationEventKinds Kind { get; private init; }

    public NotificationSeverity Severity { get; private init; }

    public EnvironmentId EnvironmentId { get; private init; }

    public StackId? StackId { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    /// <summary>One line for the history screen, e.g. "Deployment of web on prod failed". No secret values.</summary>
    public string Summary { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public NotificationStatus Status { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Why delivery failed, e.g. the bot may not post in the channel.</summary>
    public string? Error { get; private set; }

    public bool IsFinished => Status != NotificationStatus.Pending;

    /// <summary>Creates the notification a matching rule produces for the event.</summary>
    public static Notification Create(NotificationRule rule, NotificationEvent notificationEvent, string summary, DateTimeOffset now)
    {
        if (!rule.Matches(notificationEvent))
        {
            throw new DomainConflictException($"Rule {rule.Id} does not match a {notificationEvent.Kind} event.");
        }

        return new Notification(NotificationId.New(), rule.ChannelId, rule.Id, notificationEvent, NormalizeSummary(summary), now);
    }

    /// <summary>Whether it is too old to deliver; call <see cref="Expire"/> instead of sending it.</summary>
    public bool IsStale(DateTimeOffset now) => Status == NotificationStatus.Pending && now - CreatedAt >= MaxPendingAge;

    public void MarkDelivered(DateTimeOffset now) => Finish(NotificationStatus.Delivered, error: null, now);

    public void MarkFailed(string error, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new DomainValidationException("Failure reason must not be empty.");
        }

        Finish(NotificationStatus.Failed, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, now);
    }

    public void Expire(DateTimeOffset now) => Finish(NotificationStatus.Expired, error: null, now);

    private void Finish(NotificationStatus status, string? error, DateTimeOffset now)
    {
        if (IsFinished)
        {
            throw new DomainConflictException($"Notification {Id} is already {Status}.");
        }

        Status = status;
        Error = error;
        FinishedAt = now;
    }

    private static string NormalizeSummary(string summary)
    {
        var trimmed = summary.Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("Notification summary must not be empty.");
        }

        return trimmed.Length > MaxSummaryLength ? trimmed[..MaxSummaryLength] : trimmed;
    }
}
