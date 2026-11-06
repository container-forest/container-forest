using System.Globalization;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Backups;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Backups;

/// <summary>Makes one backup and records the result (ADR-0014). Who may start it is the caller's check.</summary>
public sealed partial class BackupRunner(
    SettingsStore store,
    IBackupArchiver archiver,
    IBackupStorage storage,
    IBackupRunRepository runs,
    BackupGate gate,
    SystemNotifier notifier,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    ILogger<BackupRunner> logger)
{
    /// <summary>How many runs the history keeps.</summary>
    public const int KeptRuns = 100;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>The scheduler's tick: runs a backup when one is enabled and due.</summary>
    /// <returns>The finished run; null when none was due.</returns>
    public async Task<BackupRun?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var settings = BackupSettings.From(await store.ResolveAsync(BackupSettingsSection.Instance, cancellationToken));
        if (!settings.Enabled)
        {
            return null;
        }

        var last = (await runs.ListRecentAsync(1, cancellationToken)).FirstOrDefault();
        return BackupSchedule.NextRunAt(last, settings.Interval, time.GetUtcNow()) <= time.GetUtcNow()
            ? await RunAsync(BackupTrigger.Scheduled, cancellationToken)
            : null;
    }

    /// <returns>The finished run; null when another backup is running.</returns>
    public async Task<BackupRun?> RunAsync(BackupTrigger trigger, CancellationToken cancellationToken)
    {
        if (!gate.TryEnter())
        {
            return null;
        }

        try
        {
            var startedAt = time.GetUtcNow();
            var previous = (await runs.ListRecentAsync(1, cancellationToken)).FirstOrDefault();
            var run = await AttemptAsync(trigger, startedAt, cancellationToken);

            // Saved even when the caller has gone away: the archive is already in the bucket.
            runs.Add(run);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            await runs.RemoveOlderRunsAsync(KeptRuns, CancellationToken.None);

            if (run.Outcome == BackupOutcome.Failed && BackupSchedule.ShouldNotifyFailure(previous, run.FinishedAt))
            {
                await notifier.NotifyAsync(NotificationEventKinds.BackupFailed, $"Backup of the aggregator failed: {run.Error}");
            }

            return run;
        }
        finally
        {
            gate.Exit();
        }
    }

    private async Task<BackupRun> AttemptAsync(BackupTrigger trigger, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var settings = BackupSettings.From(await store.ResolveAsync(BackupSettingsSection.Instance, timeout.Token));
            if (settings.Problems.Count > 0 || settings.Recipient is null || settings.Destination is null)
            {
                throw new BackupFailedException($"The backup is not configured. {string.Join(" ", settings.Problems)}");
            }

            using var archive = await archiver.CreateAsync(settings.Recipient, timeout.Token);
            var objectKey = ObjectKeyFor(settings.Prefix, startedAt);
            await storage.UploadAsync(settings.Destination, archive.Path, objectKey, timeout.Token);
            return BackupRun.Succeeded(trigger, startedAt, time.GetUtcNow(), objectKey, archive.SizeBytes);
        }
        catch (BackupFailedException failed)
        {
            LogFailed(failed);
            return BackupRun.Failed(trigger, startedAt, time.GetUtcNow(), failed.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BackupRun.Failed(trigger, startedAt, time.GetUtcNow(), $"Did not finish in {Timeout.TotalMinutes:0} minutes.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
            return BackupRun.Failed(trigger, startedAt, time.GetUtcNow(), "Internal error; see the aggregator's log.");
        }
    }

    /// <summary>A new name for every archive: a locked bucket refuses to overwrite one.</summary>
    internal static string ObjectKeyFor(string prefix, DateTimeOffset startedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}forest-{startedAt.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}.tar.gz.age");

    [LoggerMessage(Level = LogLevel.Error, Message = "The backup failed")]
    private partial void LogFailed(Exception exception);
}
