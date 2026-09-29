using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>
/// Decorator that runs the wrapped command only if the actor has its <see cref="IContainerCommand.RequiredPermission"/>
/// on the container's stack or, for containers outside managed stacks, on the environment.
/// This is the one permission check for every interface: the panel and the bot both go through it.
/// </summary>
public sealed class AuthorizedCommand(IContainerCommand inner, IRoleAssignmentRepository roleAssignments) : IContainerCommand
{
    public string Name => inner.Name;

    public Permission RequiredPermission => inner.RequiredPermission;

    public async Task ExecuteAsync(ContainerCommandContext context, CancellationToken cancellationToken)
    {
        var assignments = await roleAssignments.ListForUserAsync(context.Actor.Id, cancellationToken);

        var allowed = context.Stack is { } stack
            ? AccessPolicy.CanPerform(context.Actor, context.Environment, stack, assignments, RequiredPermission)
            : AccessPolicy.CanPerform(context.Actor, context.Environment, assignments, RequiredPermission);

        if (!allowed)
        {
            throw new AccessDeniedException(
                $"User '{context.Actor.UserName}' may not {Name} container '{context.Container.Name}'.");
        }

        await inner.ExecuteAsync(context, cancellationToken);
    }
}
