using System.Globalization;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>Retention and alert thresholds for administrators; nothing secret, audited and announced like other sections.</summary>
public sealed class MetricsSettingsService(
    CurrentActor currentActor,
    SettingsStore store,
    ISystemNotifier notifier,
    IAuditTrail audit)
{
    private static MetricsSettingsSection Section => MetricsSettingsSection.Instance;

    public async Task<ResolvedSettings> GetAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await store.ResolveAsync(Section, cancellationToken);
    }

    public Task<ResolvedSettings> UpdateAsync(MetricsSettings settings, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SettingsChanged, new SettingsAuditTarget(Section.Name));
        return audit.RunAsync(draft, async () =>
        {
            var actor = await currentActor.GetAdministratorAsync(cancellationToken);
            static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
            var changes = new Dictionary<string, string?>
            {
                [MetricsSettingsSection.MinuteRetentionDaysField] = N(settings.MinuteRetentionDays),
                [MetricsSettingsSection.HourRetentionDaysField] = N(settings.HourRetentionDays),
                [MetricsSettingsSection.AlertsEnabledField] = settings.AlertsEnabled ? "true" : "false",
                [MetricsSettingsSection.HostCpuPercentField] = N(settings.HostCpuPercent),
                [MetricsSettingsSection.HostMemoryPercentField] = N(settings.HostMemoryPercent),
                [MetricsSettingsSection.DiskPercentField] = N(settings.DiskPercent),
                [MetricsSettingsSection.ContainerMemoryPercentField] = N(settings.ContainerMemoryPercent),
                [MetricsSettingsSection.AlertMinutesField] = N(settings.AlertMinutes),
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
