using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>The first source of notifications: every deployment that succeeded or failed becomes an event.</summary>
public sealed class DeploymentNotifier(NotificationDispatcher dispatcher, ILogger<DeploymentNotifier> logger) : IDeploymentObserver
{
    public async Task DeploymentFinishedAsync(DeploymentOutcome outcome, CancellationToken cancellationToken)
    {
        var (deployment, stack, environment) = outcome;
        if (deployment.Status is not (DeploymentStatus.Succeeded or DeploymentStatus.Failed))
        {
            return;
        }

        try
        {
            var details = DeploymentNotificationDetails.Of(deployment, stack);
            var kind = details.Succeeded ? NotificationEventKinds.DeploymentSucceeded : NotificationEventKinds.DeploymentFailed;
            var notificationEvent = new NotificationEvent(
                kind, environment.Id, stack.Id, deployment.FinishedAt ?? deployment.QueuedAt, details);

            var created = await dispatcher.DispatchAsync(notificationEvent, Summarize(details, environment.Name), cancellationToken);
            if (created.Count > 0)
            {
                logger.LogInformation("Deployment {DeploymentId} queued {Count} notification(s)", deployment.Id, created.Count);
            }
        }
        catch (Exception ex)
        {
            // The deployment is recorded already; a lost notification must not turn into a failed run.
            logger.LogError(ex, "Cannot create notifications for deployment {DeploymentId}", deployment.Id);
        }
    }

    /// <summary>The line the history screen and the Discord message show.</summary>
    public static string Summarize(DeploymentNotificationDetails details, string environmentName)
    {
        var target = $"stack '{details.StackName}' on '{environmentName}'";
        if (details.Succeeded)
        {
            var images = details.PulledImages ? " with freshly pulled images" : "";
            return $"Deployed {target} at commit {details.Commit?.Short}{images}.";
        }

        // The reason may span lines (e.g. compose output); the summary is one line.
        var reason = string.Join(' ', details.Error!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"Deployment of {target} failed: {reason}";
    }
}
