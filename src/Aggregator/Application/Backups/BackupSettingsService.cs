using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Backups;

/// <param name="SecretAccessKey">Null keeps the stored key; empty removes it.</param>
public sealed record BackupSettingsUpdate(
    bool Enabled,
    int IntervalHours,
    string? Recipient,
    string? Endpoint,
    string? Bucket,
    string? Region,
    string? Prefix,
    string? AccessKeyId,
    string? SecretAccessKey);

/// <summary>
/// The backup section for administrators. Whoever changes it decides where a copy of every secret goes, so a change needs
/// a session rather than a token and a fresh identity confirmation, and it is audited and announced (ADR-0013).
/// </summary>
public sealed class BackupSettingsService(
    CurrentActor currentActor,
    SettingsStore store,
    IdentityConfirmations confirmations,
    SystemNotifier notifier,
    IAuditTrail audit)
{
    private static BackupSettingsSection Section => BackupSettingsSection.Instance;

    public async Task<ResolvedSettings> GetAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await store.ResolveAsync(Section, cancellationToken);
    }

    /// <exception cref="IdentityConfirmationRequiredException">The confirmation is missing or no longer valid.</exception>
    /// <exception cref="DomainValidationException">A value is invalid, or backups would be on with settings missing.</exception>
    public Task<ResolvedSettings> UpdateAsync(BackupSettingsUpdate update, string? confirmation, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SettingsChanged, new SettingsAuditTarget(Section.Name));
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            var user = await currentActor.GetSignedInUserAsync(cancellationToken);
            confirmations.Require(confirmation, user.Id);

            var changes = ToChanges(update);
            var next = BackupSettings.From(await store.PreviewAsync(Section, changes, cancellationToken));
            if (next.Enabled && next.Problems.Count > 0)
            {
                throw new DomainValidationException($"Backups cannot be turned on yet. {string.Join(" ", next.Problems)}");
            }

            var changed = await store.SaveAsync(Section, changes, cancellationToken);
            draft.Details = changed.Count > 0 ? $"Changed: {string.Join(", ", changed)}" : "Nothing changed";
            if (changed.Count > 0)
            {
                await notifier.NotifyAsync(
                    NotificationEventKinds.SettingsChanged,
                    $"Settings '{Section.Name}' were changed by {user.UserName}: {string.Join(", ", changed)}");
            }

            return await store.ResolveAsync(Section, cancellationToken);
        }, cancellationToken);
    }

    private static Dictionary<string, string?> ToChanges(BackupSettingsUpdate update)
    {
        var changes = new Dictionary<string, string?>
        {
            [BackupSettingsSection.EnabledField] = update.Enabled ? "true" : "false",
            [BackupSettingsSection.IntervalHoursField] = update.IntervalHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [BackupSettingsSection.RecipientField] = update.Recipient,
            [BackupSettingsSection.EndpointField] = update.Endpoint,
            [BackupSettingsSection.BucketField] = update.Bucket,
            [BackupSettingsSection.RegionField] = update.Region,
            [BackupSettingsSection.PrefixField] = update.Prefix,
            [BackupSettingsSection.AccessKeyIdField] = update.AccessKeyId,
        };

        // Null: leave the stored key alone. A field's value never comes back from the API, so a form has nothing to resend.
        if (update.SecretAccessKey is not null)
        {
            changes[BackupSettingsSection.SecretAccessKeyField] = update.SecretAccessKey;
        }

        return changes;
    }
}
