using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Backups;

public enum BackupTrigger
{
    Scheduled,
    Manual,
}

public enum BackupOutcome
{
    Succeeded,
    Failed,
}

/// <summary>One finished attempt to back up the aggregator (ADR-0014). Recorded at its end: a crash leaves no row, and the schedule tries again.</summary>
public sealed class BackupRun : Entity<BackupRunId>
{
    public const int MaxObjectKeyLength = 256;
    public const int MaxErrorLength = 500;

    private BackupRun(
        BackupRunId id,
        BackupTrigger trigger,
        BackupOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string? objectKey,
        long? sizeBytes,
        string? error)
        : base(id)
    {
        Trigger = trigger;
        Outcome = outcome;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        ObjectKey = objectKey;
        SizeBytes = sizeBytes;
        Error = error;
    }

    // For EF Core.
    private BackupRun() { }

    public BackupTrigger Trigger { get; private init; }

    public BackupOutcome Outcome { get; private init; }

    public DateTimeOffset StartedAt { get; private init; }

    public DateTimeOffset FinishedAt { get; private init; }

    /// <summary>Where the archive went, relative to the bucket; null for a failed run.</summary>
    public string? ObjectKey { get; private init; }

    public long? SizeBytes { get; private init; }

    /// <summary>Why it failed, written for the administrator. Never holds a secret.</summary>
    public string? Error { get; private init; }

    public static BackupRun Succeeded(BackupTrigger trigger, DateTimeOffset startedAt, DateTimeOffset finishedAt, string objectKey, long sizeBytes) =>
        new(BackupRunId.New(), trigger, BackupOutcome.Succeeded, startedAt, finishedAt, objectKey, sizeBytes, error: null);

    public static BackupRun Failed(BackupTrigger trigger, DateTimeOffset startedAt, DateTimeOffset finishedAt, string error) =>
        new(BackupRunId.New(), trigger, BackupOutcome.Failed, startedAt, finishedAt, objectKey: null, sizeBytes: null,
            error.Length > MaxErrorLength ? error[..MaxErrorLength] : error);
}

/// <summary>When the next scheduled backup is due.</summary>
public static class BackupSchedule
{
    /// <summary>A failed run is tried again this soon, not after a whole interval.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <param name="lastRun">The newest run of any outcome; null before the first one.</param>
    /// <returns>When the next run is due; a past time means now.</returns>
    public static DateTimeOffset NextRunAt(BackupRun? lastRun, TimeSpan interval, DateTimeOffset now) => lastRun switch
    {
        null => now,
        { Outcome: BackupOutcome.Succeeded } => lastRun.FinishedAt + interval,
        _ => lastRun.FinishedAt + (interval < RetryAfterFailure ? interval : RetryAfterFailure),
    };

    /// <summary>
    /// Whether a failure is worth a notification: the first of a series, then once per UTC day, so hourly retries do not flood the channel.
    /// </summary>
    /// <param name="previous">The run before the failed one; null when there was none.</param>
    public static bool ShouldNotifyFailure(BackupRun? previous, DateTimeOffset failedAt) =>
        previous is not { Outcome: BackupOutcome.Failed } || previous.FinishedAt.UtcDateTime.Date != failedAt.UtcDateTime.Date;
}
