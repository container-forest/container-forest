using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>Entry point for container actions from the panel and the bot.</summary>
public sealed class ContainerCommandService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IAgentChannel agents,
    IAuditTrail audit)
{
    /// <param name="containerId">Full id, exact name or unique id prefix, see <see cref="ContainerLookup"/>.</param>
    /// <param name="stopTimeout">For stop and restart; <see cref="StoppingContainerCommand.DefaultStopTimeout"/> when null.</param>
    public async Task ExecuteAsync(
        EnvironmentId environmentId,
        string containerId,
        ContainerAction action,
        TimeSpan? stopTimeout,
        CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);

        // An environment the user may not see is reported as missing, so its existence does not leak.
        // Checked before contacting the agent: an outsider must not be able to make agents do any work.
        if (environment is null
            || !AccessPolicy.CanSee(actor, environment, await roleAssignments.ListForUserAsync(actor.Id, cancellationToken)))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var containers = await agents.ListContainersAsync(environment.Id, cancellationToken);
        var container = ContainerLookup.Find(containers, containerId);

        var stack = container.StackName is { } name && StackName.TryParse(name, out var stackName)
            ? await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken)
            : null;

        // Audited outside the authorization, so refused attempts are recorded too.
        var command = new AuditedCommand(
            new AuthorizedCommand(Create(action, stopTimeout), roleAssignments), AuditedCommand.ActionFor(action), audit);
        await command.ExecuteAsync(new ContainerCommandContext(actor, environment, container, stack), cancellationToken);
    }

    private ContainerCommand Create(ContainerAction action, TimeSpan? stopTimeout)
    {
        var timeout = stopTimeout ?? StoppingContainerCommand.DefaultStopTimeout;
        return action switch
        {
            ContainerAction.Start => new StartContainerCommand(agents),
            ContainerAction.Stop => new StopContainerCommand(agents, timeout),
            ContainerAction.Restart => new RestartContainerCommand(agents, timeout),
            _ => throw new DomainValidationException($"Unknown container action {action}."),
        };
    }
}
