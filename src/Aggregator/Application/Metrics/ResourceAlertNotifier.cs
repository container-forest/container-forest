using System.Globalization;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>
/// Turns a <see cref="ResourceAlert"/> into notifications. A container's alert is its managed stack's event when the
/// compose project is one, as for container incidents; everything else is the environment's.
/// </summary>
public sealed class ResourceAlertNotifier(
    NotificationDispatcher dispatcher,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    ILogger<ResourceAlertNotifier> logger)
{
    public async Task<IReadOnlyList<Notification>> NotifyAsync(ResourceAlert alert, CancellationToken cancellationToken)
    {
        var environment = await environments.FindAsync(alert.EnvironmentId, cancellationToken);
        if (environment is null)
        {
            return [];
        }

        StackName.TryParse(alert.StackName, out var stackName);
        var stack = stackName is null ? null : await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken);
        var details = new ResourceUsageNotificationDetails(alert.Alert, alert.Subject, stackName, Math.Round(alert.Percent, 1), alert.ThresholdPercent);
        var notificationEvent = new NotificationEvent(alert.Kind, environment.Id, stack?.Id, alert.At, details);

        var created = await dispatcher.DispatchAsync(notificationEvent, Summarize(alert.Kind, details, environment.Name), cancellationToken);
        if (created.Count > 0)
        {
            logger.LogInformation(
                "{Kind} ({Alert} {Subject}) in environment {EnvironmentId} queued {Count} notification(s)",
                alert.Kind, alert.Alert, alert.Subject, environment.Id, created.Count);
        }

        return created;
    }

    /// <summary>The line the history screen and the Discord message show.</summary>
    public static string Summarize(NotificationEventKinds kind, ResourceUsageNotificationDetails details, string environmentName)
    {
        var percent = details.Percent.ToString("0", CultureInfo.InvariantCulture);
        var stack = details.StackName is null ? "" : $" of stack '{details.StackName}'";
        var what = details.Alert switch
        {
            ResourceAlertKind.HostCpu => $"CPU of '{environmentName}'",
            ResourceAlertKind.HostMemory => $"Memory of '{environmentName}'",
            ResourceAlertKind.Disk => $"Disk '{details.Subject}' of '{environmentName}'",
            ResourceAlertKind.ContainerMemory => $"Memory of container '{details.Subject}'{stack} on '{environmentName}'",
            _ => throw new ArgumentOutOfRangeException(nameof(details), details.Alert, "Unknown alert."),
        };
        var limit = details.Alert == ResourceAlertKind.ContainerMemory ? " of its limit" : "";
        return kind == NotificationEventKinds.ResourceUsageHigh
            ? $"{what} is at {percent}%{limit} (threshold {details.ThresholdPercent}%)."
            : $"{what} is back under {details.ThresholdPercent}%{limit} ({percent}% now).";
    }
}
