using System.Globalization;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>
/// Polling of registries for administrators (ADR-0013, ADR-0015). Nothing secret is in the section, so a session or a
/// token of an administrator may change it; it is audited and announced like every settings change.
/// </summary>
public sealed class ImageUpdateSettingsService(
    CurrentActor currentActor,
    SettingsStore store,
    ISystemNotifier notifier,
    IAuditTrail audit)
{
    private static ImageUpdateSettingsSection Section => ImageUpdateSettingsSection.Instance;

    public async Task<ResolvedSettings> GetAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await store.ResolveAsync(Section, cancellationToken);
    }

    public Task<ResolvedSettings> UpdateAsync(bool pollingEnabled, int pollingIntervalMinutes, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SettingsChanged, new SettingsAuditTarget(Section.Name));
        return audit.RunAsync(draft, async () =>
        {
            var actor = await currentActor.GetAdministratorAsync(cancellationToken);
            var changes = new Dictionary<string, string?>
            {
                [ImageUpdateSettingsSection.PollingEnabledField] = pollingEnabled ? "true" : "false",
                [ImageUpdateSettingsSection.PollingIntervalMinutesField] = pollingIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            };
            var changed = await store.SaveAsync(Section, changes, cancellationToken);
            draft.Details = changed.Count > 0 ? $"Changed: {string.Join(", ", changed)}" : "Nothing changed";
            if (changed.Count > 0)
            {
                await notifier.NotifyAsync(
                    NotificationEventKinds.SettingsChanged,
                    $"Settings '{Section.Name}' were changed by {actor.User.UserName}: {string.Join(", ", changed)}");
            }

            return await store.ResolveAsync(Section, cancellationToken);
        }, cancellationToken);
    }
}
