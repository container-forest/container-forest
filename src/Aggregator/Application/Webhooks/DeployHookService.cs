using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Webhooks;

public enum DeployHookOutcome
{
    /// <summary>A deployment was queued.</summary>
    Queued,

    /// <summary>A queued deployment that pulls every image has not started yet; it will pull the new ones.</summary>
    AlreadyQueued,

    /// <summary>A deployment is running or queued without pulling; the hook deploys after it.</summary>
    AfterRunning,
}

/// <param name="DeploymentId">The queued deployment, for <see cref="DeployHookOutcome.Queued"/>.</param>
public sealed record DeployHookResult(StackId StackId, DeployHookOutcome Outcome, DeploymentId? DeploymentId);

/// <summary>
/// Receives calls to a stack's deploy hook: CI pushed a new image and asks for the stack to be deployed with fresh
/// images (redeploy with pull of the branch head). Anonymous: the stack's token is the authentication, and only its
/// hash is stored.
/// </summary>
/// <remarks>
/// For images under a moving tag, which a push webhook (pulling only missing images) does not update. Not a user
/// action, like a push: an editor of the stack created the hook; the call only says when.
/// </remarks>
public sealed partial class DeployHookService(
    IStackRepository stacks,
    IDeploymentRepository deployments,
    IAgentChannel agents,
    IUnitOfWork unitOfWork,
    IDeploymentQueue queue,
    PushDeploymentGate gate,
    TimeProvider time,
    ILogger<DeployHookService> logger)
{
    /// <param name="token">The token the caller presented; null when it presented none.</param>
    /// <exception cref="WebhookRejectedException">
    /// No such stack, no deploy hook or a wrong token: one answer for all, so the endpoint does not tell which stacks exist.
    /// </exception>
    /// <exception cref="AgentOfflineException">The agent is offline; nothing is queued (invariant 7).</exception>
    public async Task<DeployHookResult> ReceiveAsync(StackId stackId, string? token, CancellationToken cancellationToken)
    {
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        if (stack is null || token is null || !stack.AcceptsDeployHook(token))
        {
            if (stack?.HasDeployHook == true)
            {
                LogRejected(stack.Name.Value);
            }

            throw new WebhookRejectedException();
        }

        using var _ = await gate.EnterAsync(cancellationToken);

        // Under the gate, as for pushes: PushFollowUpObserver takes what is marked here after the deployment's
        // outcome is saved, so the call is deployed either by a new deployment or after the unfinished one.
        switch (await deployments.FindUnfinishedAsync(stack.Id, cancellationToken))
        {
            // It has not pulled yet and will pull everything.
            case { Status: DeploymentStatus.Queued, PullPolicy: ImagePullPolicy.Always }:
                return new DeployHookResult(stack.Id, DeployHookOutcome.AlreadyQueued, null);
            case not null:
                gate.MarkPending(stack.Id, ImagePullPolicy.Always);
                return new DeployHookResult(stack.Id, DeployHookOutcome.AfterRunning, null);
        }

        if (!agents.IsOnline(stack.EnvironmentId))
        {
            throw new AgentOfflineException(stack.EnvironmentId);
        }

        var deployment = Deployment.QueueFromDeployHook(stack.Id, time.GetUtcNow());
        deployments.Add(deployment);

        // Saved before queueing: the worker loads the deployment from the database.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        queue.Enqueue(deployment.Id);
        LogQueued(stack.Name.Value);
        return new DeployHookResult(stack.Id, DeployHookOutcome.Queued, deployment.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a deploy hook call for stack {Stack}: missing or wrong token")]
    private partial void LogRejected(string stack);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deploy hook of stack {Stack} queued a deployment")]
    private partial void LogQueued(string stack);
}
