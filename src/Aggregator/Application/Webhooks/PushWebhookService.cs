using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Webhooks;

/// <summary>
/// The delivery is not from the git server: no such repository or connection, no webhook secret, an unknown sender or
/// a wrong signature. Deliberately one answer for all of them, so the endpoint does not tell which ones exist.
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

    /// <summary>The stack's environment may no longer deploy from the repository's connection (ADR-0006).</summary>
    NotAllowed,

    /// <summary>The stack is held at the commit of a rollback; pushes do not deploy it until someone deploys the branch.</summary>
    Held,
}

/// <param name="DeploymentId">The queued deployment, for <see cref="PushStackOutcome.Queued"/>.</param>
public sealed record PushStackResult(
    StackId StackId, EnvironmentId EnvironmentId, StackName Name, PushStackOutcome Outcome, DeploymentId? DeploymentId);

/// <param name="Provider">The git server the delivery came from.</param>
/// <param name="Detail">Why an event was ignored; null otherwise.</param>
public sealed record WebhookResult(
    string Provider, WebhookOutcome Outcome, string? Detail, IReadOnlyList<PushStackResult> Stacks);

/// <summary>
/// Receives push webhooks: checks the delivery against the secret of the repository, or of its connection for an
/// organization-wide webhook (ADR-0006), and queues a deployment of each stack that deploys the pushed branch on push.
/// Anonymous: the signature is the authentication. The deployment always fetches the branch head instead of trusting
/// the pushed commit, so a replayed old delivery redeploys the current head rather than rolling the stack back.
/// </summary>
/// <remarks>
/// Not a user action, like rehydration: an administrator or editor set the secret and an editor of the stack turned
/// <see cref="Stack.DeployOnPush"/> on; the push only says when.
/// </remarks>
public sealed partial class PushWebhookService(
    IEnumerable<GitWebhookProvider> providers,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
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
    /// <summary>A delivery of the repository's own webhook.</summary>
    /// <exception cref="WebhookRejectedException">The delivery is not authentic.</exception>
    /// <exception cref="WebhookPayloadException">An authentic push that cannot be read.</exception>
    public async Task<WebhookResult> ReceiveAsync(RepositoryId repositoryId, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var repository = await repositories.FindAsync(repositoryId, cancellationToken);
        var provider = Authenticate(repository?.ProtectedWebhookSecret, delivery, $"repository {repositoryId}");
        if (Ignore(provider, delivery) is { } ignored)
        {
            return ignored;
        }

        var push = provider.ReadPush(delivery);
        if (IgnorePush(provider, push) is { } ignoredPush)
        {
            return ignoredPush;
        }

        var connection = repository!.ConnectionId is { } connectionId ? await connections.FindAsync(connectionId, cancellationToken) : null;
        var results = await DeployRepositoryAsync(repository, connection, push, cancellationToken);
        LogPush(provider.Name, repository.Name, push.Branch!.Value, push.After!.Short, results.Count);
        return new WebhookResult(provider.Name, WebhookOutcome.Push, null, results);
    }

    /// <summary>
    /// A delivery of an organization-wide webhook: the payload names the pushed repository, which is looked up among
    /// the connection's repositories by its address. The payload can be trusted only because its signature is checked first.
    /// </summary>
    /// <exception cref="WebhookRejectedException">The delivery is not authentic.</exception>
    /// <exception cref="WebhookPayloadException">An authentic push that cannot be read.</exception>
    public async Task<WebhookResult> ReceiveForConnectionAsync(
        GitConnectionId connectionId, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(connectionId, cancellationToken);
        var provider = Authenticate(connection?.ProtectedWebhookSecret, delivery, $"connection {connectionId}");
        if (Ignore(provider, delivery) is { } ignored)
        {
            return ignored;
        }

        var push = provider.ReadPush(delivery);
        if (IgnorePush(provider, push) is { } ignoredPush)
        {
            return ignoredPush;
        }

        var keys = push.RepositoryUrls.Select(GitUrl.Key).ToHashSet();
        var pushed = (await repositories.ListByConnectionAsync(connectionId, cancellationToken))
            .Where(r => keys.Contains(GitUrl.Key(r.Url)))
            .ToList();
        if (pushed.Count == 0)
        {
            return new WebhookResult(
                provider.Name, WebhookOutcome.Ignored, "No repository of this connection has the pushed repository's address.", []);
        }

        var results = new List<PushStackResult>();
        foreach (var repository in pushed)
        {
            var deployed = await DeployRepositoryAsync(repository, connection, push, cancellationToken);
            LogPush(provider.Name, repository.Name, push.Branch!.Value, push.After!.Short, deployed.Count);
            results.AddRange(deployed);
        }

        return new WebhookResult(provider.Name, WebhookOutcome.Push, null, results);
    }

    /// <summary>Checks the delivery against the protected secret; any failure is the same rejection.</summary>
    private GitWebhookProvider Authenticate(string? protectedSecret, WebhookDelivery delivery, string target)
    {
        var provider = providers.FirstOrDefault(p => p.Recognizes(delivery));
        if (protectedSecret is null || provider is null)
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
            LogSecretUnreadable(target);
            throw new WebhookRejectedException();
        }

        if (!provider.IsAuthentic(delivery, secret))
        {
            LogRejected(provider.Name, target);
            throw new WebhookRejectedException();
        }

        return provider;
    }

    private static WebhookResult? Ignore(GitWebhookProvider provider, WebhookDelivery delivery) => provider.EventKind(delivery) switch
    {
        WebhookEventKind.Ping => new WebhookResult(provider.Name, WebhookOutcome.Pong, null, []),
        WebhookEventKind.Other => new WebhookResult(provider.Name, WebhookOutcome.Ignored, "Only push events deploy.", []),
        _ => null,
    };

    private static WebhookResult? IgnorePush(GitWebhookProvider provider, PushEvent push) =>
        push.Branch is null ? new WebhookResult(provider.Name, WebhookOutcome.Ignored, "Not a push to a branch.", [])
        : push.After is null ? new WebhookResult(provider.Name, WebhookOutcome.Ignored, "The branch was deleted.", [])
        : null;

    private async Task<List<PushStackResult>> DeployRepositoryAsync(
        GitRepository repository, GitConnection? connection, PushEvent push, CancellationToken cancellationToken)
    {
        var results = new List<PushStackResult>();
        foreach (var stack in await stacks.ListDeployedOnPushAsync(repository.Id, push.Branch!, cancellationToken))
        {
            var (outcome, deployment) = repository.IsUsableIn(stack.EnvironmentId, connection)
                ? await DeployAsync(stack, push.After!, cancellationToken)
                : (PushStackOutcome.NotAllowed, null);
            results.Add(new PushStackResult(stack.Id, stack.EnvironmentId, stack.Name, outcome, deployment?.Id));
        }

        return results;
    }

    private async Task<(PushStackOutcome Outcome, Deployment? Deployment)> DeployAsync(
        Stack stack, CommitSha pushed, CancellationToken cancellationToken)
    {
        if (stack.IsHeld)
        {
            return (PushStackOutcome.Held, null);
        }

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
                gate.MarkPending(stack.Id, ImagePullPolicy.Missing);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook delivery for {Target}: wrong secret or signature")]
    private partial void LogRejected(string provider, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "The webhook secret of {Target} cannot be decrypted; set a new one")]
    private partial void LogSecretUnreadable(string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} push to {Repository}@{Branch} ({Commit}) for {Stacks} stack(s)")]
    private partial void LogPush(string provider, string repository, string branch, string commit, int stacks);
}

/// <summary>
/// Deploys a push or a deploy hook call that arrived while the stack was being deployed (<see cref="PushDeploymentGate"/>).
/// </summary>
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
            if (gate.TakePending(stack.Id) is not { } pull)
            {
                return;
            }

            // A rollback that ran meanwhile holds the stack: the push or hook call must not undo it.
            if (stack.IsHeld)
            {
                LogHeld(stack.Name.Value);
                return;
            }

            // Someone deployed in between: that deployment started after the push and fetches its commit. It pulls
            // the hook's new images only if it pulls them all; otherwise the hook waits for it in turn.
            if (await deployments.FindUnfinishedAsync(stack.Id, cancellationToken) is { } next)
            {
                if (pull == ImagePullPolicy.Always && next.PullPolicy != ImagePullPolicy.Always)
                {
                    gate.MarkPending(stack.Id, pull);
                }

                return;
            }

            if (!agents.IsOnline(stack.EnvironmentId))
            {
                LogAgentOffline(stack.Name.Value);
                return;
            }

            var deployment = pull == ImagePullPolicy.Always
                ? Deployment.QueueFromDeployHook(stack.Id, time.GetUtcNow())
                : Deployment.QueueFromWebhook(stack.Id, time.GetUtcNow());
            deployments.Add(deployment);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            queue.Enqueue(deployment.Id);
        }
        catch (Exception ex)
        {
            LogFailed(ex, stack.Name.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A push or deploy hook call for stack {Stack} arrived during its deployment, but the agent is now offline; it is not deployed")]
    private partial void LogAgentOffline(string stack);

    [LoggerMessage(Level = LogLevel.Information, Message = "A push or deploy hook call for stack {Stack} arrived during a rollback, which now holds the stack; it is not deployed")]
    private partial void LogHeld(string stack);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not queue the deployment of a push or deploy hook call for stack {Stack}")]
    private partial void LogFailed(Exception exception, string stack);
}
