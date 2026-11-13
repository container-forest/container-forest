using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;

namespace ContainerForest.Aggregator.Application.Audit;

/// <summary>
/// Decorator that records every attempt of the wrapped container command in the audit log: done, denied by
/// <see cref="AuthorizedCommand"/> or failed. Wraps the authorization so that refused attempts are recorded too.
/// </summary>
public sealed class AuditedCommand(IContainerCommand inner, AuditAction action, IAuditTrail audit) : IContainerCommand
{
    public string Name => inner.Name;

    public Permission RequiredPermission => inner.RequiredPermission;

    public Task ExecuteAsync(ContainerCommandContext context, CancellationToken cancellationToken)
    {
        var target = new ContainerAuditTarget(context.Container.Name, context.Environment.Id, context.Stack?.Id);
        return audit.RunAsync(new AuditDraft(action, target), () => inner.ExecuteAsync(context, cancellationToken), cancellationToken);
    }

    /// <summary>The audit action for a container action.</summary>
    public static AuditAction ActionFor(ContainerAction action) => action switch
    {
        ContainerAction.Start => AuditAction.ContainerStarted,
        ContainerAction.Stop => AuditAction.ContainerStopped,
        ContainerAction.Restart => AuditAction.ContainerRestarted,
        ContainerAction.Remove => AuditAction.ContainerRemoved,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown container action."),
    };
}
