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

/// <summary>A container that died or became unhealthy, as the agent reported it.</summary>
/// <param name="ContainerName">Docker's name without the leading slash; the short id when the container is already gone.</param>
/// <param name="StackName">The compose project; null for a container outside compose. It need not be a managed stack.</param>
/// <param name="ServiceName">The compose service; null outside compose.</param>
/// <param name="ExitCode">Set for a container that died, when the agent's report still had it.</param>
/// <param name="OutOfMemory">The kernel killed the container for exceeding its memory limit (Docker's "oom" event came with the death).</param>
public sealed record ContainerNotificationDetails(
    string ContainerId,
    string ContainerName,
    StackName? StackName,
    string? ServiceName,
    int? ExitCode,
    bool OutOfMemory = false) : NotificationDetails
{
    public override bool Fits(NotificationEventKinds kind) => kind switch
    {
        NotificationEventKinds.ContainerDied => true,
        NotificationEventKinds.ContainerUnhealthy => ExitCode is null && !OutOfMemory,
        _ => false,
    };
}
