using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>Container logs for the panel, as the current user is allowed to read them.</summary>
/// <remarks>
/// Logs can contain secrets that applications print themselves; the Viewer role already includes them
/// (see the access table in docs/architecture.md), so the permission is checked like any other.
/// </remarks>
public sealed class ContainerLogService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IAgentChannel agents)
{
    public const int DefaultTailLines = 200;

    /// <summary>Upper bound for one request: the aggregator holds the lines in memory and sends them in one response.</summary>
    public const int MaxTailLines = 5000;

    /// <param name="containerId">Full id, exact name or unique id prefix, see <see cref="ContainerLookup"/>.</param>
    /// <param name="tailLines">How many of the last lines; <see cref="DefaultTailLines"/> when null.</param>
    /// <param name="since">Only lines written at or after this moment.</param>
    /// <returns>Oldest first.</returns>
    public async Task<IReadOnlyList<ContainerLogLine>> ReadAsync(
        EnvironmentId environmentId,
        string containerId,
        int? tailLines,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var tail = tailLines ?? DefaultTailLines;
        if (tail is < 1 or > MaxTailLines)
        {
            throw new DomainValidationException($"The number of lines must be between 1 and {MaxTailLines}.");
        }

        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        // As for container actions: an invisible environment is missing, and the agent is not asked anything.
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var container = ContainerLookup.Find(await agents.ListContainersAsync(environment.Id, cancellationToken), containerId);
        var stack = container.StackName is { } name && StackName.TryParse(name, out var stackName)
            ? await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken)
            : null;

        // A container of a managed stack is governed by the stack's role, any other by the environment's (as in the list).
        var visible = stack is not null
            ? AccessPolicy.CanSee(actor, environment, stack, assignments)
            : AccessPolicy.CanPerform(actor, environment, assignments, Permission.View);
        if (!visible)
        {
            throw new NotFoundException("Container", $"'{containerId}'");
        }

        var mayRead = stack is not null
            ? AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.ViewLogs)
            : AccessPolicy.CanPerform(actor, environment, assignments, Permission.ViewLogs);
        if (!mayRead)
        {
            throw new AccessDeniedException($"Reading the logs of '{container.Name}' requires the Viewer role or higher.");
        }

        return await agents.ReadContainerLogsAsync(
            environment.Id, container.Id, new ContainerLogQuery(tail, since), cancellationToken);
    }
}
