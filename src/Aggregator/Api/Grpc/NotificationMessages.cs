using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Contracts.Bot;
using BotNotification = ContainerForest.Contracts.Bot.Notification;
using BotSeverity = ContainerForest.Contracts.Bot.NotificationSeverity;
using NotificationSeverity = ContainerForest.Aggregator.Domain.Notifications.NotificationSeverity;

namespace ContainerForest.Aggregator.Api.Grpc;

/// <summary>Turns notifications into the messages of bot.proto.</summary>
internal static class NotificationMessages
{
    public static BotNotification ToMessage(OutgoingNotification outgoing)
    {
        var notification = outgoing.Notification;
        var message = new BotNotification
        {
            Id = notification.Id.ToString(),
            DiscordChannelId = outgoing.DiscordChannelId,
            Kind = ToKind(notification.Kind),
            Severity = ToSeverity(notification.Severity),
            OccurredUnixMs = notification.OccurredAt.ToUnixTimeMilliseconds(),
            EnvironmentId = notification.EnvironmentId?.ToString() ?? "",
            EnvironmentName = outgoing.EnvironmentName,
            Summary = notification.Summary,
        };

        if (notification.Details is DeploymentNotificationDetails deployment)
        {
            message.Deployment = new DeploymentDetails
            {
                DeploymentId = deployment.DeploymentId.ToString(),
                StackId = deployment.StackId.ToString(),
                StackName = deployment.StackName.Value,
                CommitSha = deployment.Commit?.Value ?? "",
                PulledImages = deployment.PulledImages,
                Error = deployment.Error ?? "",
            };
        }
        else if (notification.Details is ContainerNotificationDetails container)
        {
            message.Container = new ContainerDetails
            {
                ContainerId = container.ContainerId,
                ContainerName = container.ContainerName,
                StackName = container.StackName?.Value ?? "",
                ServiceName = container.ServiceName ?? "",
                System = container.IsSystem,
            };
            if (container.ExitCode is { } exitCode)
            {
                message.Container.ExitCode = exitCode;
            }
        }

        return message;
    }

    private static NotificationKind ToKind(NotificationEventKinds kind) => kind switch
    {
        NotificationEventKinds.DeploymentSucceeded => NotificationKind.DeploymentSucceeded,
        NotificationEventKinds.DeploymentFailed => NotificationKind.DeploymentFailed,
        NotificationEventKinds.ContainerDied => NotificationKind.ContainerDied,
        NotificationEventKinds.ContainerUnhealthy => NotificationKind.ContainerUnhealthy,
        NotificationEventKinds.AgentOffline => NotificationKind.AgentOffline,
        NotificationEventKinds.AgentOnline => NotificationKind.AgentOnline,
        NotificationEventKinds.ContainerHealthy => NotificationKind.ContainerHealthy,
        NotificationEventKinds.BackupFailed => NotificationKind.BackupFailed,
        NotificationEventKinds.SettingsChanged => NotificationKind.SettingsChanged,
        NotificationEventKinds.AccountSecurityChanged => NotificationKind.AccountSecurityChanged,
        NotificationEventKinds.ImageUpdateFailed => NotificationKind.ImageUpdateFailed,
        NotificationEventKinds.ImagePinned => NotificationKind.ImagePinned,
        NotificationEventKinds.ResourceUsageHigh => NotificationKind.ResourceUsageHigh,
        NotificationEventKinds.ResourceUsageNormal => NotificationKind.ResourceUsageNormal,
        _ => NotificationKind.Unspecified,
    };

    private static BotSeverity ToSeverity(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Info => BotSeverity.Info,
        NotificationSeverity.Warning => BotSeverity.Warning,
        NotificationSeverity.Error => BotSeverity.Error,
        _ => BotSeverity.Unspecified,
    };
}
