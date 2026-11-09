using System.Text;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Application.Webhooks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>Who asked for a check.</summary>
public enum ImageUpdateSource
{
    /// <summary>The poller, every polling interval.</summary>
    Poll,

    /// <summary>The stack's image hook, called by CI.</summary>
    Hook,

    /// <summary>A user who may deploy the stack, from the panel or the CLI.</summary>
    User,
}

/// <param name="Tag">An exact tag to update to; null to take the newest the policy allows.</param>
/// <param name="DryRun">Only say what would change; nothing is committed.</param>
/// <param name="UserName">The user's name, for <see cref="ImageUpdateSource.User"/>.</param>
/// <param name="AllowDowngrade">The hook may ask for an older tag (<see cref="Stack.ImageHookAllowsDowngrade"/>).</param>
public sealed record ImageUpdateRequest(
    ImageUpdateSource Source,
    string? Tag = null,
    bool DryRun = false,
    UserId? UserId = null,
    string? UserName = null,
    bool AllowDowngrade = false);

public enum ImageUpdateOutcome
{
    /// <summary>The compose file already has the tag the check selected.</summary>
    UpToDate,

    /// <summary>A dry run: <see cref="ImageUpdateResult.TargetTag"/> would be committed.</summary>
    WouldUpdate,

    /// <summary>The update commit was pushed and is being deployed.</summary>
    Updated,

    /// <summary>The image is pinned; automatic updates and the hook leave it alone.</summary>
    Pinned,

    /// <summary>
    /// Nothing to do yet: not every candidate's creation time is read (the next checks read the rest), or no tag
    /// matches the pattern.
    /// </summary>
    Waiting,

    /// <summary>The check failed; <see cref="ImageUpdateResult.Detail"/> says why, and the policy shows it.</summary>
    Failed,
}

/// <param name="CurrentTag">The tag in the compose file at the branch head; null when unknown.</param>
/// <param name="TargetTag">The tag the check selected or was asked for.</param>
/// <param name="Commit">The update commit, for <see cref="ImageUpdateOutcome.Updated"/>.</param>
/// <param name="DeploymentId">The deployment queued for it; null when a running or queued one deploys it.</param>
public sealed record ImageUpdateResult(
    ImagePolicyId PolicyId,
    ImageName Image,
    ImageUpdateOutcome Outcome,
    string? CurrentTag = null,
    string? TargetTag = null,
    string? Detail = null,
    CommitSha? Commit = null,
    DeploymentId? DeploymentId = null);

/// <summary>
/// Runs one check of an image policy (ADR-0015): reads the branch head and the registry, and when a newer tag (or the
/// requested one) is not in the compose file yet, commits it signed with the aggregator's key, pushes it and deploys
/// exactly that commit. Callers check who may ask; this service checks what the policy allows.
/// </summary>
public sealed partial class ImageUpdateService(
    IImagePolicyRepository policies,
    IImageUpdateRecordRepository records,
    IImageTagCache tagCache,
    IStackRepository stacks,
    IEnvironmentRepository environments,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
    IRegistryCredentialRepository registryCredentials,
    IGitSource git,
    IGitWriter writer,
    IRegistryClient registry,
    IComposeImageRewriter rewriter,
    ICommitSigner signer,
    AggregatorSigningKeyService signingKeys,
    CommitEndorsementService endorsements,
    IDeploymentRepository deployments,
    IDeploymentQueue queue,
    PushDeploymentGate gate,
    IAgentChannel agents,
    NotificationDispatcher notifications,
    ISecretProtector protector,
    ImageUpdateLocks locks,
    ImageUpdateOptions options,
    DeploymentOptions deploymentOptions,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    ILogger<ImageUpdateService> logger)
{
    /// <summary>Pushes refused because the branch moved, before the update fails.</summary>
    public const int MaxPushAttempts = 3;

    public const string HookName = "image hook";

    /// <exception cref="NotFoundException">No such policy.</exception>
    /// <exception cref="DomainValidationException">The requested tag does not match the pattern or is not in the registry.</exception>
    /// <exception cref="DomainConflictException">The hook asks for an older tag without being allowed to.</exception>
    /// <exception cref="AgentOfflineException">The stack's agent is offline; nothing is committed (invariant 7).</exception>
    public async Task<ImageUpdateResult> CheckAsync(ImagePolicyId policyId, ImageUpdateRequest request, CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(policyId, cancellationToken);
        using var _ = await locks.EnterAsync(policy.StackId, cancellationToken);
        if (request.Source != ImageUpdateSource.User && policy.IsPinned)
        {
            return new ImageUpdateResult(policy.Id, policy.Image, ImageUpdateOutcome.Pinned, policy.PinnedTag, policy.PinnedTag,
                $"Pinned at {policy.PinnedTag} by {policy.PinnedBy}; unpin it to resume updates.");
        }

        var context = await LoadAsync(policy, cancellationToken);
        try
        {
            return await CheckAsync(context, request, cancellationToken);
        }
        catch (Exception ex) when (ex is RegistryUnavailableException or RegistryNotFoundException or ImagePolicyException or DeploymentFailedException)
        {
            var registryDown = ex is RegistryUnavailableException;
            var warning = ex is RegistryUnavailableException { RateLimited: true }
                ? "The registry limits requests (429): ordering by the tag's timestamp needs far fewer of them."
                : policy.Warning;
            if (policy.RecordFailed(ex.Message, registryDown, time.GetUtcNow()))
            {
                await NotifyAsync(context, NotificationEventKinds.ImageUpdateFailed, $"Image update of {policy.Image} failed: {ex.Message}");
            }

            if (warning != policy.Warning)
            {
                policy.RecordWarning(warning);
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            LogFailed(context.Stack.Name.Value, policy.Image.Value, ex.Message);
            return new ImageUpdateResult(policy.Id, policy.Image, ImageUpdateOutcome.Failed, Detail: ex.Message);
        }
    }

    private async Task<ImageUpdateResult> CheckAsync(UpdateContext context, ImageUpdateRequest request, CancellationToken cancellationToken)
    {
        var (policy, stack) = (context.Policy, context.Stack);
        var head = await git.FetchAsync(context.Repository, GitAccessResolver.ForFetch(context.Connection, protector), stack.Branch, cancellationToken);
        var yaml = await ReadComposeAsync(context, head, cancellationToken);
        var slots = rewriter.Find(yaml, policy);
        var current = slots[0];
        var auth = await RegistryAuthAsync(context, cancellationToken);

        var (target, pin, warning) = request.Tag is { } tag
            ? await ExplicitAsync(context, request, tag, current, auth, cancellationToken)
            : await NewestAsync(policy, auth, cancellationToken);
        if (target is null)
        {
            policy.RecordChecked(time.GetUtcNow(), null, warning?.Warning);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new ImageUpdateResult(policy.Id, policy.Image, ImageUpdateOutcome.Waiting, current.Tag, Detail: warning?.Problem);
        }

        var value = policy.PinDigest ? $"{target.Tag}@{target.Digest}" : target.Tag;
        var upToDate = slots.All(s => s.Tag == target.Tag && s.Digest == (policy.PinDigest ? target.Digest : null));
        if (upToDate || request.DryRun)
        {
            if (!request.DryRun)
            {
                policy.RecordChecked(time.GetUtcNow(), target.Tag, warning?.Warning);
                await PinIfAskedAsync(context, request, pin, target.Tag);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new ImageUpdateResult(
                policy.Id, policy.Image, upToDate ? ImageUpdateOutcome.UpToDate : ImageUpdateOutcome.WouldUpdate, current.Tag, target.Tag,
                upToDate ? null : $"{string.Join(", ", slots.Select(s => s.Service))}: {current.Value} → {value}");
        }

        if (!agents.IsOnline(stack.EnvironmentId))
        {
            throw new AgentOfflineException(stack.EnvironmentId);
        }

        var push = GitAccessResolver.ForPush(context.Connection, protector);
        var (record, commit) = await CommitAndPushAsync(context, request, head, yaml, target, value, push, cancellationToken);
        var deployment = await QueueAsync(stack, commit, record, cancellationToken);
        policy.RecordChecked(time.GetUtcNow(), target.Tag, warning?.Warning);
        await PinIfAskedAsync(context, request, pin, target.Tag);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogUpdated(stack.Name.Value, policy.Image.Value, target.Tag, commit.Short);
        return new ImageUpdateResult(
            policy.Id, policy.Image, ImageUpdateOutcome.Updated, current.Tag, target.Tag,
            $"{string.Join(", ", record.Changes.Select(c => c.Service))}: {current.Value} → {value}", commit, deployment?.Id);
    }

    private async Task<(ImageUpdateRecord Record, CommitSha Commit)> CommitAndPushAsync(
        UpdateContext context,
        ImageUpdateRequest request,
        CommitSha head,
        string yaml,
        ImageCandidate target,
        string value,
        GitAccess push,
        CancellationToken cancellationToken)
    {
        var (policy, stack) = (context.Policy, context.Stack);
        var (key, privateKey) = await signingKeys.GetActiveAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            await EnsureEndorsedAsync(context, head, cancellationToken);
            var rewrite = rewriter.Rewrite(yaml, policy, value);
            var reason = request.Source switch
            {
                ImageUpdateSource.User => ImageUpdateReason.User,
                ImageUpdateSource.Hook when request.Tag is not null => ImageUpdateReason.Hook,
                _ => ImageUpdateReason.Policy,
            };
            var recordId = ImageUpdateRecordId.New();
            var name = policy.Image.Repository[(policy.Image.Repository.LastIndexOf('/') + 1)..];
            var describe = reason switch
            {
                ImageUpdateReason.Policy => "policy",
                ImageUpdateReason.Hook => $"requested by {HookName}",
                _ => $"pinned by {request.UserName}",
            };
            var message = $"deploy: {name}:{target.Tag} ({describe})\n\nForest-Image-Update: {recordId}\n";
            var commit = await writer.CreateCommitAsync(
                context.Repository,
                new GitFileCommit(head, stack.ComposePath.Value, new UTF8Encoding(false).GetBytes(rewrite.Yaml), message,
                    options.AuthorName, options.AuthorEmail, time.GetUtcNow()),
                data => signer.SignAsync(privateKey, data, cancellationToken),
                cancellationToken);

            // Saved before the push: the git server's webhook may deploy the commit before the push call even returns.
            var record = ImageUpdateRecord.Create(
                recordId, stack.Id, policy.Id, commit, head, stack.ComposePath, target.Tag, policy.PinDigest ? target.Digest : null,
                rewrite.Changes, reason, request.Source == ImageUpdateSource.User ? request.UserId : null,
                request.Source == ImageUpdateSource.User ? request.UserName : null, time.GetUtcNow());
            records.Add(record);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            try
            {
                await writer.PushAsync(context.Repository, push, commit, stack.Branch, cancellationToken);
                LogSigned(commit.Short, key.Fingerprint);
                return (record, commit);
            }
            catch (GitPushRejectedException ex) when (attempt < MaxPushAttempts)
            {
                // Someone pushed in between: the update is made again on their commit, which is checked like the first.
                LogRetrying(stack.Name.Value, ex.Message);
                head = await git.FetchAsync(context.Repository, GitAccessResolver.ForFetch(context.Connection, protector), stack.Branch, cancellationToken);
                yaml = await ReadComposeAsync(context, head, cancellationToken);
                rewriter.Find(yaml, policy);
            }
            catch (GitPushRejectedException ex)
            {
                throw new ImagePolicyException($"The git server refused the update commit {MaxPushAttempts} times: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The update goes on top of the head, so the head must be endorsed as an automatic deployment would need it
    /// (ADR-0011): otherwise the aggregator's update would carry content nobody endorsed into a deployment.
    /// </summary>
    private async Task EnsureEndorsedAsync(UpdateContext context, CommitSha head, CancellationToken cancellationToken)
    {
        if (!context.Stack.RequireSignedCommits)
        {
            return;
        }

        var result = await endorsements.EvaluateAsync(
            context.Repository, context.Environment, context.Stack, head, StackOperation.Redeploy, cancellationToken);
        if (!result.IsEndorsed && !await deployments.WasApprovedAsync(context.Stack.Id, head, cancellationToken))
        {
            throw new ImagePolicyException(
                $"The stack requires signed commits and the head of {context.Stack.Branch} is not endorsed ({result.Detail}); " +
                "no update is made on it.");
        }

        if (!await endorsements.DescendsFromDeployedAsync(context.Repository, context.Stack, head, cancellationToken))
        {
            throw new ImagePolicyException(
                $"The head {head.Short} of {context.Stack.Branch} does not descend from the deployed commit; no update is made on it.");
        }
    }

    /// <summary>
    /// Deploys the update commit, as the deploy hook would: a queued deployment that fetches the head, or a running one
    /// of this commit, takes it; otherwise one is queued after the running deployment, or now, pinned to the commit.
    /// </summary>
    private async Task<Deployment?> QueueAsync(Stack stack, CommitSha commit, ImageUpdateRecord record, CancellationToken cancellationToken)
    {
        using var _ = await gate.EnterAsync(cancellationToken);
        switch (await deployments.FindUnfinishedAsync(stack.Id, cancellationToken))
        {
            case { Status: DeploymentStatus.Queued, IsPinned: false }:
            case { } running when running.Commit == commit:
                return null;
            case not null:
                gate.MarkPending(stack.Id, ImagePullPolicy.Missing);
                return null;
        }

        if (stack.DeployedCommit == commit)
        {
            return null;
        }

        var deployment = Deployment.QueueFromImageUpdate(stack.Id, commit, record.Id, time.GetUtcNow());
        deployments.Add(deployment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        queue.Enqueue(deployment.Id);
        return deployment;
    }

    private async Task<(ImageCandidate? Target, bool Pin, SelectionNote? Note)> NewestAsync(
        ImagePolicy policy, RegistryAuth? auth, CancellationToken cancellationToken)
    {
        var tags = await registry.ListTagsAsync(policy.Image, auth, cancellationToken);
        var candidates = ImageTagSelector.Candidates(policy, tags);
        ImageSelection selection;
        string? warning = null;
        if (policy.Order == ImageTagOrder.TagTimestamp)
        {
            selection = ImageTagSelector.ByTimestamp(policy, candidates);
        }
        else
        {
            var known = await tagCache.ListAsync(policy.Image, cancellationToken);
            var read = new Dictionary<string, ImageTagCacheEntry>(known, StringComparer.Ordinal);
            foreach (var tag in ImageTagSelector.ToRead(candidates, known, options.MaxTagReadsPerCheck))
            {
                read[tag] = await CacheAsync(policy.Image, tag, auth, cancellationToken);
            }

            selection = ImageTagSelector.ByCreated(policy, candidates, read);
            if (candidates.Count > ImageTagSelector.ManyCandidates)
            {
                warning = $"{candidates.Count} tags match: ordering by creation time reads a manifest for every new one; " +
                    "a timestamp in the tag needs none.";
            }
        }

        switch (selection.Outcome)
        {
            case ImageSelectionOutcome.Selected:
                var newest = selection.Newest!;
                if (newest.Digest is null)
                {
                    var image = await CacheAsync(policy.Image, newest.Tag, auth, cancellationToken, readCreated: false);
                    newest = newest with { Digest = image.Digest };
                }

                return (newest, false, warning is null ? null : new SelectionNote(warning, null));
            case ImageSelectionOutcome.Ambiguous:
                throw new ImagePolicyException(selection.Problem!);
            default:
                return (null, false, new SelectionNote(warning, selection.Problem));
        }
    }

    private async Task<(ImageCandidate? Target, bool Pin, SelectionNote? Note)> ExplicitAsync(
        UpdateContext context, ImageUpdateRequest request, string tag, ComposeImageSlot current, RegistryAuth? auth, CancellationToken cancellationToken)
    {
        var policy = context.Policy;
        if (!policy.Allows(tag))
        {
            throw new DomainValidationException($"Tag '{tag}' does not match the policy's pattern {policy.TagPattern}.");
        }

        ImageTagCacheEntry entry;
        try
        {
            entry = await CacheAsync(policy.Image, tag, auth, cancellationToken, readCreated: policy.Order == ImageTagOrder.ImageCreated);
        }
        catch (RegistryNotFoundException)
        {
            throw new DomainValidationException($"The registry has no tag '{tag}' of {policy.Image}.");
        }

        var target = Candidate(policy, entry);
        var older = false;
        if (current.Tag is { } currentTag && currentTag != tag && policy.Allows(currentTag))
        {
            var known = await tagCache.ListAsync(policy.Image, cancellationToken);
            var deployed = known.TryGetValue(currentTag, out var cached)
                ? Candidate(policy, cached)
                : policy.Order == ImageTagOrder.TagTimestamp
                    ? new ImageCandidate(currentTag, null, null, policy.TryReadTimestamp(currentTag, out var ts) ? ts : null)
                    : await TryCandidateAsync(policy, currentTag, auth, cancellationToken);
            older = deployed is not null && ImageTagSelector.Compare(policy, target, deployed) < 0;
        }

        if (older && request.Source == ImageUpdateSource.Hook && !request.AllowDowngrade)
        {
            throw new DomainConflictException(
                $"Tag '{tag}' is older than the deployed '{current.Tag}'; the stack's image hook may not roll back unless it is allowed to.");
        }

        // A person's choice, or a rollback, holds: the next check would otherwise bring the newest tag straight back.
        return (target, request.Source == ImageUpdateSource.User || older, null);
    }

    private async Task<ImageCandidate?> TryCandidateAsync(ImagePolicy policy, string tag, RegistryAuth? auth, CancellationToken cancellationToken)
    {
        try
        {
            return Candidate(policy, await CacheAsync(policy.Image, tag, auth, cancellationToken));
        }
        catch (RegistryNotFoundException)
        {
            // The deployed tag is gone from the registry: nothing to compare with, so the request is not a rollback.
            return null;
        }
    }

    private static ImageCandidate Candidate(ImagePolicy policy, ImageTagCacheEntry entry) =>
        new(entry.Tag, entry.Digest, entry.Created, policy.TryReadTimestamp(entry.Tag, out var ts) ? ts : null);

    private async Task<ImageTagCacheEntry> CacheAsync(
        ImageName image, string tag, RegistryAuth? auth, CancellationToken cancellationToken, bool readCreated = true)
    {
        var known = await tagCache.ListAsync(image, cancellationToken);
        if (known.TryGetValue(tag, out var cached) && (cached.Created is not null || !readCreated))
        {
            return cached;
        }

        var read = await registry.GetImageAsync(image, tag, auth, readCreated, cancellationToken);
        var entry = ImageTagCacheEntry.Create(image, tag, read.Digest, read.Created ?? cached?.Created, time.GetUtcNow());
        await tagCache.PutAsync(entry, cancellationToken);
        return entry;
    }

    private async Task PinIfAskedAsync(UpdateContext context, ImageUpdateRequest request, bool pin, string tag)
    {
        var policy = context.Policy;
        if (!pin || (policy.PinnedTag == tag && request.Source != ImageUpdateSource.User))
        {
            return;
        }

        var by = request.Source == ImageUpdateSource.User ? request.UserName ?? "a user" : HookName;
        policy.Pin(tag, by, time.GetUtcNow());
        await NotifyAsync(context, NotificationEventKinds.ImagePinned, $"{policy.Image} is pinned at {tag} by {by}; automatic updates of it stop.");
    }

    private async Task<string> ReadComposeAsync(UpdateContext context, CommitSha commit, CancellationToken cancellationToken)
    {
        var path = context.Stack.ComposePath;
        var snapshot = await git.ReadAsync(context.Repository, commit, path.Directory, deploymentOptions.MaxBundleBytes, cancellationToken);
        var file = snapshot.Files.FirstOrDefault(f => f.Path.Value == path.FileName)
            ?? throw new ImagePolicyException($"The compose file {path} does not exist at commit {commit.Short}.");
        if (file.IsSecret)
        {
            throw new ImagePolicyException($"The compose file {path} is encrypted; the aggregator cannot change it.");
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(file.Content);
    }

    private async Task<RegistryAuth?> RegistryAuthAsync(UpdateContext context, CancellationToken cancellationToken)
    {
        var credential = (await registryCredentials.ListByEnvironmentAsync(context.Stack.EnvironmentId, cancellationToken))
            .FirstOrDefault(c => c.Server == context.Policy.Image.Registry);
        if (credential is null)
        {
            return null;
        }

        try
        {
            return new RegistryAuth(credential.Username, protector.Unprotect(SecretPurpose.RegistryPassword, credential.ProtectedPassword));
        }
        catch (SecretUnreadableException)
        {
            throw new ImagePolicyException(
                $"The stored password for registry '{credential.Server}' cannot be decrypted; set the credentials for this environment again.");
        }
    }

    private async Task<UpdateContext> LoadAsync(ImagePolicy policy, CancellationToken cancellationToken)
    {
        var stack = await stacks.GetAsync(policy.StackId, cancellationToken);
        var environment = await environments.GetAsync(stack.EnvironmentId, cancellationToken);
        var repository = await repositories.GetAsync(stack.RepositoryId, cancellationToken);
        var connection = repository.ConnectionId is { } connectionId ? await connections.FindAsync(connectionId, cancellationToken) : null;
        return new UpdateContext(policy, stack, environment, repository, connection);
    }

    private async Task NotifyAsync(UpdateContext context, NotificationEventKinds kind, string summary)
    {
        try
        {
            await notifications.DispatchAsync(
                new NotificationEvent(kind, context.Stack.EnvironmentId, context.Stack.Id, time.GetUtcNow()),
                $"{context.Stack.Name}: {summary}",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(ex, kind);
        }
    }

    private sealed record UpdateContext(
        ImagePolicy Policy, Stack Stack, DockerEnvironment Environment, GitRepository Repository, GitConnection? Connection);

    /// <param name="Warning">Kept on the policy until the next check.</param>
    /// <param name="Problem">Why no tag was selected.</param>
    private sealed record SelectionNote(string? Warning, string? Problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stack {Stack}: updated {Image} to {Tag} in commit {Commit}")]
    private partial void LogUpdated(string stack, string image, string tag, string commit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Signed update commit {Commit} with the aggregator's key {Fingerprint}")]
    private partial void LogSigned(string commit, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stack {Stack}: the update commit was refused, trying again on the new head: {Reason}")]
    private partial void LogRetrying(string stack, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stack {Stack}: image update of {Image} failed: {Reason}")]
    private partial void LogFailed(string stack, string image, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create a {Kind} notification")]
    private partial void LogNotifyFailed(Exception exception, NotificationEventKinds kind);
}
