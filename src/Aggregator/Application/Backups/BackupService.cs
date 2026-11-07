using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Backups;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Backups;

/// <param name="NextRunAt">Null while backups are off.</param>
/// <param name="NotifiesFailures">A rule on the whole system sends failed backups somewhere.</param>
/// <param name="NotifiesChanges">Rules on the whole system send changes of settings and of account security somewhere.</param>
public sealed record BackupStatus(
    bool Enabled,
    IReadOnlyList<string> Problems,
    bool IsRunning,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastSucceededAt,
    bool NotifiesFailures,
    bool NotifiesChanges,
    IReadOnlyList<BackupRun> Runs);

/// <summary>The state of backups and running one by hand. Administrators only.</summary>
public sealed class BackupService(
    CurrentActor currentActor,
    SettingsStore store,
    BackupRunner runner,
    BackupGate gate,
    IBackupRunRepository runs,
    INotificationRuleRepository rules,
    IAuditTrail audit,
    TimeProvider time)
{
    public const int ShownRuns = 20;

    public async Task<BackupStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var settings = BackupSettings.From(await store.ResolveAsync(BackupSettingsSection.Instance, cancellationToken));
        var recent = await runs.ListRecentAsync(ShownRuns, cancellationToken);
        var lastSucceeded = await runs.FindLastSucceededAsync(cancellationToken);
        var now = time.GetUtcNow();
        var active = await rules.ListActiveAsync(cancellationToken);

        bool Notifies(NotificationEventKinds kind) =>
            active.Any(rule => rule.Matches(new NotificationEvent(kind, null, null, now)));

        return new BackupStatus(
            settings.Enabled,
            settings.Problems,
            gate.IsBusy,
            settings.Enabled ? BackupSchedule.NextRunAt(recent.FirstOrDefault(), settings.Interval, now) : null,
            lastSucceeded?.FinishedAt,
            Notifies(NotificationEventKinds.BackupFailed),
            Notifies(NotificationEventKinds.SettingsChanged) && Notifies(NotificationEventKinds.AccountSecurityChanged),
            recent);
    }

    /// <summary>
    /// Makes a backup now, on or off schedule: it is also how to check the settings before turning the schedule on.
    /// Runs to its end even if the request goes away.
    /// </summary>
    /// <exception cref="DomainConflictException">The settings are incomplete, or another backup is running.</exception>
    public Task<BackupRun> RunNowAsync(CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.BackupRequested, new SettingsAuditTarget(BackupSettingsSection.SectionName));
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            var settings = BackupSettings.From(await store.ResolveAsync(BackupSettingsSection.Instance, cancellationToken));
            if (settings.Problems.Count > 0)
            {
                throw new DomainConflictException($"The backup is not configured. {string.Join(" ", settings.Problems)}");
            }

            var run = await runner.RunAsync(BackupTrigger.Manual, CancellationToken.None)
                ?? throw new DomainConflictException("Another backup is running.");
            if (run.Error is { } error)
            {
                draft.Fail(error);
            }

            return run;
        }, cancellationToken);
    }
}
