using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Notifications;

/// <summary>
/// What an event is about beyond its kind and scope, e.g. which deployment failed and why. Kept with the
/// notification as a snapshot: a notification delivered after the bot reconnects says what was true when it happened.
/// </summary>
public abstract record NotificationDetails
{
    /// <summary>Whether these details can describe an event of this kind.</summary>
    public abstract bool Fits(NotificationEventKinds kind);
}

/// <summary>A finished deployment: <see cref="Error"/> is set exactly when it failed.</summary>
/// <param name="Commit">Null when the deployment failed before the commit was known, e.g. on a missing branch.</param>
/// <param name="Error">The deployment's failure reason; it never contains secret values (see <see cref="Deployment.Error"/>).</param>
public sealed record DeploymentNotificationDetails(
    DeploymentId DeploymentId,
    StackId StackId,
    StackName StackName,
    CommitSha? Commit,
    bool PulledImages,
    string? Error) : NotificationDetails
{
    public bool Succeeded => Error is null;

    /// <summary>The details of a deployment that succeeded or failed; cancelled and unfinished ones are not notified about.</summary>
    public static DeploymentNotificationDetails Of(Deployment deployment, Stack stack)
    {
        if (deployment.StackId != stack.Id)
        {
            throw new DomainValidationException($"Deployment {deployment.Id} is not of stack {stack.Id}.");
        }

        var error = deployment.Status switch
        {
            DeploymentStatus.Succeeded => null,
            DeploymentStatus.Failed => deployment.Error,
            _ => throw new DomainConflictException($"Deployment {deployment.Id} is {deployment.Status}; only succeeded and failed ones are notified about."),
        };

        return new DeploymentNotificationDetails(
            deployment.Id, stack.Id, stack.Name, deployment.Commit, deployment.PullPolicy == ImagePullPolicy.Always, error);
    }

    public override bool Fits(NotificationEventKinds kind) => kind switch
    {
        NotificationEventKinds.DeploymentSucceeded => Succeeded,
        NotificationEventKinds.DeploymentFailed => !Succeeded,
        _ => false,
    };
}
