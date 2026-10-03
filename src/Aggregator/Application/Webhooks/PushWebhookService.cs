using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Webhooks;

/// <summary>
/// The delivery is not from the repository's git server: no such repository, no webhook secret, an unknown sender or
/// a wrong signature. Deliberately one answer for all of them, so the endpoint does not tell which repositories exist.
/// </summary>
public sealed class WebhookRejectedException() : Exception("The webhook delivery could not be authenticated.");

public enum WebhookOutcome
{
    /// <summary>The git server checked the webhook works.</summary>
    Pong,

    /// <summary>Not a push to a branch (another event, a tag, a deleted branch); nothing happens.</summary>
    Ignored,

    /// <summary>A push to a branch; <see cref="WebhookResult.Stacks"/> says what happened to each stack.</summary>
    Push,
}

public enum PushStackOutcome
{
    /// <summary>A deployment was queued.</summary>
    Queued,

    /// <summary>A queued deployment has not fetched yet; it will deploy this push.</summary>
    AlreadyQueued,

    /// <summary>A running deployment has already fetched an older commit; the push is deployed after it.</summary>
    AfterRunning,

    /// <summary>A running deployment is deploying this very commit.</summary>
    AlreadyRunning,

    /// <summary>The stack already runs the pushed commit, e.g. a redelivery.</summary>
    AlreadyDeployed,

    /// <summary>The agent is offline; nothing is queued (invariant 7).</summary>
    AgentOffline,
}

/// <param name="DeploymentId">The queued deployment, for <see cref="PushStackOutcome.Queued"/>.</param>
public sealed record PushStackResult(
    StackId StackId, EnvironmentId EnvironmentId, StackName Name, PushStackOutcome Outcome, DeploymentId? DeploymentId);

/// <param name="Provider">The git server the delivery came from.</param>
/// <param name="Detail">Why an event was ignored; null otherwise.</param>
public sealed record WebhookResult(
    string Provider, WebhookOutcome Outcome, string? Detail, IReadOnlyList<PushStackResult> Stacks);

/// <summary>
/// Receives push webhooks: checks the delivery against the repository's secret and queues a deployment of each stack
/// that deploys the pushed branch on push. Anonymous: the signature is the authentication. The deployment always
/// fetches the branch head instead of trusting the pushed commit, so a replayed old delivery redeploys the current
/// head rather than rolling the stack back.
/// </summary>
/// <remarks>
/// Not a user action, like rehydration: an administrator set the secret and an editor of the stack turned
/// <see cref="Stack.DeployOnPush"/> on; the push only says when.
/// </remarks>
public sealed partial class PushWebhookService(
    IEnumerable<GitWebhookProvider> providers,
    IGitRepositoryRepository repositories,
    IStackRepository stacks,
    IDeploymentRepository deployments,
    IAgentChannel agents,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    IDeploymentQueue queue,
    PushDeploymentGate gate,
    TimeProvider time,
    ILogger<PushWebhookService> logger)
{
    /// <exception cref="WebhookRejectedException">The delivery is not authentic.</exception>
    /// <exception cref="WebhookPayloadException">An authentic push that cannot be read.</exception>
    public async Task<WebhookResult> ReceiveAsync(RepositoryId repositoryId, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var repository = await repositories.FindAsync(repositoryId, cancellationToken);
        var provider = providers.FirstOrDefault(p => p.Recognizes(delivery));
        if (repository?.ProtectedWebhookSecret is not { } protectedSecret || provider is null)
        {
            throw new WebhookRejectedException();
        }

        string secret;
        try
        {
            secret = protector.Unprotect(SecretPurpose.WebhookSecret, protectedSecret);
        }
        catch (SecretUnreadableException)
        {
            LogSecretUnreadable(repositoryId.Value);
            throw new WebhookRejectedException();
        }

        if (!provider.IsAuthentic(delivery, secret))
        {
            LogRejected(provider.Name, repositoryId.Value);
            throw new WebhookRejectedException();
        }

        switch (provider.EventKind(delivery))
        {
            case WebhookEventKind.Ping:
                return new WebhookResult(provider.Name, WebhookOutcome.Pong, null, []);
            case WebhookEventKind.Other:
                return new WebhookResult(provider.Name, WebhookOutcome.Ignored, "Only push events deploy.", []);
        }

        var push = provider.ReadPush(delivery);
        if (push.Branch is null)
        {
            return new WebhookResult(provider.Name, WebhookOutcome.Ignored, "Not a push to a branch.", []);
        }

        if (push.After is null)
        {
            return new WebhookResult(provider.Name, WebhookOutcome.Ignored, "The branch was deleted.", []);
        }

        var results = new List<PushStackResult>();
        foreach (var stack in await stacks.ListDeployedOnPushAsync(repository.Id, push.Branch, cancellationToken))
        {
            var (outcome, deployment) = await DeployAsync(stack, push.After, cancellationToken);
            results.Add(new PushStackResult(stack.Id, stack.EnvironmentId, stack.Name, outcome, deployment?.Id));
        }

        LogPush(provider.Name, repository.Name, push.Branch.Value, push.After.Short, results.Count);
        return new WebhookResult(provider.Name, WebhookOutcome.Push, null, results);
    }

    private async Task<(PushStackOutcome Outcome, Deployment? Deployment)> DeployAsync(
        Stack stack, CommitSha pushed, CancellationToken cancellationToken)
    {
        using var _ = await gate.EnterAsync(cancellationToken);

        // The running deployment's outcome is saved before the gate hears about it, so this read and the
        // follow-up in PushFollowUpObserver cannot both miss the push.
        switch (await deployments.FindUnfinishedAsync(stack.Id, cancellationToken))
        {
            // The status changes to Running and is saved before the branch is fetched.
            case { Status: DeploymentStatus.Queued }:
                return (PushStackOutcome.AlreadyQueued, null);
            case { } running when running.Commit == pushed:
                return (PushStackOutcome.AlreadyRunning, null);
            case not null:
                gate.MarkPending(stack.Id);
                return (PushStackOutcome.AfterRunning, null);
        }

        if (stack.DeployedCommit == pushed)
        {
            return (PushStackOutcome.AlreadyDeployed, null);
        }

        if (!agents.IsOnline(stack.EnvironmentId))
        {
            return (PushStackOutcome.AgentOffline, null);
        }

        var deployment = Deployment.QueueFromWebhook(stack.Id, time.GetUtcNow());
        deployments.Add(deployment);

        // Saved before queueing: the worker loads the deployment from the database.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        queue.Enqueue(deployment.Id);
        return (PushStackOutcome.Queued, deployment);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook delivery for repository {RepositoryId}: wrong secret or signature")]
    private partial void LogRejected(string provider, Guid repositoryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The webhook secret of repository {RepositoryId} cannot be decrypted; set a new one")]
    private partial void LogSecretUnreadable(Guid repositoryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} push to {Repository}@{Branch} ({Commit}) for {Stacks} stack(s)")]
    private partial void LogPush(string provider, string repository, string branch, string commit, int stacks);
}

/// <summary>Deploys a push that arrived while the stack was being deployed (<see cref="PushDeploymentGate"/>).</summary>
public sealed partial class PushFollowUpObserver(
    PushDeploymentGate gate,
    IDeploymentRepository deployments,
    IAgentChannel agents,
    IUnitOfWork unitOfWork,
    IDeploymentQueue queue,
    TimeProvider time,
    ILogger<PushFollowUpObserver> logger) : IDeploymentObserver
{
    public async Task DeploymentFinishedAsync(DeploymentOutcome outcome, CancellationToken cancellationToken)
    {
        var stack = outcome.Stack;
        try
        {
            using var _ = await gate.EnterAsync(cancellationToken);
            if (!gate.TakePending(stack.Id))
            {
                return;
            }

            // Someone deployed in between: that deployment fetches after the push.
            if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
            {
                return;
            }

            if (!agents.IsOnline(stack.EnvironmentId))
            {
                LogAgentOffline(stack.Name.Value);
                return;
            }

            var deployment = Deployment.QueueFromWebhook(stack.Id, time.GetUtcNow());
            deployments.Add(deployment);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            queue.Enqueue(deployment.Id);
        }
        catch (Exception ex)
        {
            LogFailed(ex, stack.Name.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A push to stack {Stack} arrived during its deployment, but the agent is now offline; it is not deployed")]
    private partial void LogAgentOffline(string stack);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not queue the deployment of a push to stack {Stack}")]
    private partial void LogFailed(Exception exception, string stack);
}
