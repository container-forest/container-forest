using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>Starting deployments and reading their history, as the current user is allowed to.</summary>
public sealed class DeploymentService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IDeploymentRepository deployments,
    IUnitOfWork unitOfWork,
    IDeploymentQueue queue,
    IAgentChannel agents,
    IAuditTrail audit,
    TimeProvider time)
{
    public const int HistoryLimit = 50;

    /// <summary>Queues a deployment of the stack; the background worker runs it.</summary>
    /// <exception cref="AgentOfflineException">
    /// The agent is offline. The deployment is not queued: it would run whenever the agent returns (invariant 7).
    /// </exception>
    public async Task<Deployment> QueueAsync(StackId stackId, ImagePullPolicy pullPolicy, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadVisibleStackAsync(stackId, cancellationToken);
        var draft = new AuditDraft(AuditAction.DeploymentQueued, new StackAuditTarget(stack.Id, stack.Name.Value, environment.Id))
        {
            Details = pullPolicy == ImagePullPolicy.Always ? "Redeploy with pull" : "Redeploy",
        };
        return await audit.RunAsync(
            draft, () => QueueVisibleAsync(actor, stack, environment, assignments, pullPolicy, cancellationToken), cancellationToken);
    }

    private async Task<Deployment> QueueVisibleAsync(
        Actor actor,
        Stack stack,
        DockerEnvironment environment,
        IReadOnlyList<RoleAssignment> assignments,
        ImagePullPolicy pullPolicy,
        CancellationToken cancellationToken)
    {

        var operation = pullPolicy == ImagePullPolicy.Always ? StackOperation.RedeployWithPull : StackOperation.Redeploy;
        if (!AccessPolicy.CanPerform(actor, environment, stack, assignments, operation))
        {
            throw new AccessDeniedException($"Deploying stack '{stack.Name}' requires the Editor role.");
        }

        stack.EnsureAllows(operation);
        if (stack.RequiresUpdateHelper(operation))
        {
            throw new DomainConflictException(
                $"System stack '{stack.Name}' can only be updated through the helper container, which is not implemented yet.");
        }

        if (!agents.IsOnline(environment.Id))
        {
            throw new AgentOfflineException(environment.Id);
        }

        if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
        {
            throw new DomainConflictException($"Stack '{stack.Name}' already has a deployment in progress.");
        }

        var deployment = Deployment.QueueManual(stack.Id, actor.Id, pullPolicy, time.GetUtcNow());
        deployments.Add(deployment);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        queue.Enqueue(deployment.Id);
        return deployment;
    }

    /// <summary>The stack's latest deployments, newest first.</summary>
    public async Task<IReadOnlyList<DeploymentSummary>> ListAsync(StackId stackId, CancellationToken cancellationToken)
    {
        await LoadVisibleStackAsync(stackId, cancellationToken);
        return await deployments.ListByStackAsync(stackId, HistoryLimit, cancellationToken);
    }

    /// <summary>One deployment with its log.</summary>
    public async Task<Deployment> GetAsync(DeploymentId id, CancellationToken cancellationToken)
    {
        var deployment = await deployments.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Deployment", id);
        try
        {
            await LoadVisibleStackAsync(deployment.StackId, cancellationToken);
        }
        catch (NotFoundException)
        {
            // Same answer as for a missing deployment, so its existence does not leak.
            throw new NotFoundException("Deployment", id);
        }

        return deployment;
    }

    /// <exception cref="NotFoundException">The stack does not exist or the user may not see it.</exception>
    private async Task<VisibleStack> LoadVisibleStackAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        var environment = stack is null ? null : await environments.FindAsync(stack.EnvironmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        if (stack is null || environment is null || !AccessPolicy.CanSee(actor, environment, stack, assignments))
        {
            throw new NotFoundException("Stack", stackId);
        }

        return new VisibleStack(actor, stack, environment, assignments);
    }

    private sealed record VisibleStack(
        Actor Actor, Stack Stack, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments);
}
