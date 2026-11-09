using System.Text;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Images;

/// <param name="CanChange">Whether the current user may change the policies of the stack, check, pin and unpin.</param>
public sealed record ImagePolicyList(IReadOnlyList<ImagePolicy> Policies, bool CanChange, IReadOnlyList<ImageUpdateRecord> RecentUpdates);

/// <summary>
/// Image policies of a stack as its users see them (ADR-0015): an Editor of the stack adds, changes and removes them,
/// checks them now, pins and unpins. Every change is in the audit log.
/// </summary>
public sealed class ImagePolicyService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
    IImagePolicyRepository policies,
    IImageUpdateRecordRepository records,
    IGitSource git,
    IComposeImageRewriter rewriter,
    ISecretProtector protector,
    ImageUpdateService updates,
    ImageUpdateLocks locks,
    NotificationDispatcher notifications,
    DeploymentOptions deploymentOptions,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    public const int RecentUpdatesLimit = 20;

    public async Task<ImagePolicyList> ListAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var loaded = await LoadVisibleAsync(stackId, cancellationToken);
        return new ImagePolicyList(
            await policies.ListByStackAsync(stackId, cancellationToken),
            loaded.CanChange,
            await records.ListByStackAsync(stackId, RecentUpdatesLimit, cancellationToken));
    }

    /// <exception cref="DomainValidationException">The image or settings are not valid, or the compose file has no service to update.</exception>
    /// <exception cref="DomainConflictException">A system stack, a policy for the image exists, or the stack has the most policies.</exception>
    public Task<ImagePolicy> AddAsync(StackId stackId, string image, ImagePolicySettings settings, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImagePolicyAdded) { Details = $"{image?.Trim()} · {settings.TagPattern}" };
        return audit.RunAsync(draft, async () =>
        {
            var loaded = await LoadChangeableAsync(stackId, cancellationToken);
            draft.Target = Target(loaded.Stack);
            var name = ImageName.Parse(image);
            var existing = await policies.ListByStackAsync(stackId, cancellationToken);
            if (existing.Any(p => p.Image == name))
            {
                throw new DomainConflictException($"Stack '{loaded.Stack.Name}' already has a policy for {name}.");
            }

            if (existing.Count >= ImagePolicy.MaxPerStack)
            {
                throw new DomainConflictException($"A stack has at most {ImagePolicy.MaxPerStack} image policies.");
            }

            var policy = ImagePolicy.Create(stackId, name, settings, loaded.Actor.Id, loaded.ByAdministrator, time.GetUtcNow());
            await ValidateAsync(loaded, policy, cancellationToken);
            policies.Add(policy);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return policy;
        }, cancellationToken);
    }

    public Task<ImagePolicy> ChangeAsync(ImagePolicyId policyId, ImagePolicySettings settings, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImagePolicyChanged);
        return audit.RunAsync(draft, async () =>
        {
            var (policy, loaded) = await LoadPolicyAsync(policyId, cancellationToken);
            draft.Target = Target(loaded.Stack);
            draft.Details = $"{policy.Image}: {Describe(policy.Settings)} → {Describe(settings)}";
            using var _ = await locks.EnterAsync(policy.StackId, cancellationToken);
            policy.Change(settings, loaded.Actor.Id, loaded.ByAdministrator);
            await ValidateAsync(loaded, policy, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return policy;
        }, cancellationToken);
    }

    public Task RemoveAsync(ImagePolicyId policyId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImagePolicyRemoved);
        return audit.RunAsync(draft, async () =>
        {
            var (policy, loaded) = await LoadPolicyAsync(policyId, cancellationToken);
            draft.Target = Target(loaded.Stack);
            draft.Details = policy.Image.Value;
            using var _ = await locks.EnterAsync(policy.StackId, cancellationToken);
            policies.Remove(policy);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Checks the policy now, as polling would; with <paramref name="dryRun"/>, only says what would change.</summary>
    public async Task<ImageUpdateResult> CheckAsync(ImagePolicyId policyId, bool dryRun, CancellationToken cancellationToken)
    {
        var (policy, loaded) = await LoadPolicyAsync(policyId, cancellationToken, requireChange: !dryRun);
        var request = new ImageUpdateRequest(ImageUpdateSource.Poll, DryRun: dryRun, UserId: loaded.Actor.Id, UserName: loaded.Actor.User.UserName);
        if (dryRun)
        {
            return await updates.CheckAsync(policy.Id, request, cancellationToken);
        }

        var draft = new AuditDraft(AuditAction.DeploymentQueued, Target(loaded.Stack)) { Details = $"Image check of {policy.Image}" };
        return await audit.RunAsync(draft, async () =>
        {
            var result = await updates.CheckAsync(policy.Id, request, cancellationToken);
            Describe(draft, result);
            return result;
        }, cancellationToken);
    }

    /// <summary>Updates to the tag and pins it there: automatic updates of the image stop until it is unpinned.</summary>
    public async Task<ImageUpdateResult> PinAsync(ImagePolicyId policyId, string tag, CancellationToken cancellationToken)
    {
        var (policy, loaded) = await LoadPolicyAsync(policyId, cancellationToken);
        var draft = new AuditDraft(AuditAction.ImagePinned, Target(loaded.Stack)) { Details = $"{policy.Image}:{tag}" };
        return await audit.RunAsync(draft, async () =>
        {
            var result = await updates.CheckAsync(
                policy.Id,
                new ImageUpdateRequest(ImageUpdateSource.User, tag?.Trim(), UserId: loaded.Actor.Id, UserName: loaded.Actor.User.UserName),
                cancellationToken);
            Describe(draft, result);
            return result;
        }, cancellationToken);
    }

    public Task<ImagePolicy> UnpinAsync(ImagePolicyId policyId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImageUnpinned);
        return audit.RunAsync(draft, async () =>
        {
            var (policy, loaded) = await LoadPolicyAsync(policyId, cancellationToken);
            draft.Target = Target(loaded.Stack);
            draft.Details = $"{policy.Image}:{policy.PinnedTag}";
            using var _ = await locks.EnterAsync(policy.StackId, cancellationToken);
            if (!policy.IsPinned)
            {
                return policy;
            }

            policy.Unpin();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await notifications.DispatchAsync(
                new NotificationEvent(NotificationEventKinds.ImagePinned, loaded.Stack.EnvironmentId, loaded.Stack.Id, time.GetUtcNow()),
                $"{loaded.Stack.Name}: {policy.Image} is unpinned by {loaded.Actor.User.UserName}; automatic updates of it resume.",
                CancellationToken.None);
            return policy;
        }, cancellationToken);
    }

    private static void Describe(AuditDraft draft, ImageUpdateResult result)
    {
        draft.Details = $"{draft.Details}: {result.Outcome}" + (result.TargetTag is null ? "" : $" {result.TargetTag}");
        if (result.Outcome == ImageUpdateOutcome.Failed)
        {
            draft.Fail(result.Detail ?? "The image update failed.");
        }
    }

    /// <summary>
    /// A policy must find something to update at the branch head, and the aggregator must be able to push the result
    /// (ADR-0015, items 1 and 5): a policy that could never work is refused now rather than failing on every check.
    /// </summary>
    private async Task ValidateAsync(LoadedStack loaded, ImagePolicy policy, CancellationToken cancellationToken)
    {
        var stack = loaded.Stack;
        if (stack.IsSystem)
        {
            throw new DomainConflictException($"System stack '{stack.Name}' is updated through the helper container; it cannot have image policies.");
        }

        if (stack.RequireImageDigests && !policy.PinDigest)
        {
            throw new DomainValidationException($"Stack '{stack.Name}' requires image digests, so the policy must pin them.");
        }

        var repository = await repositories.GetAsync(stack.RepositoryId, cancellationToken);
        var connection = repository.ConnectionId is { } id ? await connections.FindAsync(id, cancellationToken) : null;
        if (connection is not { HasPushCredential: true })
        {
            throw new DomainValidationException(
                connection is null
                    ? $"Repository '{repository.Name}' is public; image updates need a git connection with a push token."
                    : $"Connection '{connection.Name}' has no push token; an administrator sets one before image policies can be added.");
        }

        try
        {
            var head = await git.FetchAsync(repository, GitAccessResolver.ForFetch(connection, protector), stack.Branch, cancellationToken);
            var path = stack.ComposePath;
            var snapshot = await git.ReadAsync(repository, head, path.Directory, deploymentOptions.MaxBundleBytes, cancellationToken);
            var file = snapshot.Files.FirstOrDefault(f => f.Path.Value == path.FileName)
                ?? throw new ImagePolicyException($"The compose file {path} does not exist at the head of {stack.Branch}.");
            var slots = rewriter.Find(new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(file.Content), policy);
            if (policy.Services.FirstOrDefault(name => slots.All(s => s.Service != name)) is { } missing)
            {
                throw new ImagePolicyException($"Service '{missing}' of the compose file does not use {policy.Image}.");
            }
        }
        catch (Exception ex) when (ex is ImagePolicyException or DeploymentFailedException)
        {
            throw new DomainValidationException(ex.Message);
        }
    }

    private static string Describe(ImagePolicySettings s) =>
        $"{s.TagPattern} {s.Order}" + (s.Services.Count > 0 ? $" [{string.Join(", ", s.Services)}]" : "")
        + (s.Poll ? " poll" : "") + (s.AcceptRequests ? " hook" : "") + (s.PinDigest ? " digest" : "");

    private static StackAuditTarget Target(Stack stack) => new(stack.Id, stack.Name.Value, stack.EnvironmentId);

    private async Task<(ImagePolicy Policy, LoadedStack Stack)> LoadPolicyAsync(
        ImagePolicyId policyId, CancellationToken cancellationToken, bool requireChange = true)
    {
        var policy = await policies.FindAsync(policyId, cancellationToken) ?? throw new NotFoundException("Image policy", policyId);
        try
        {
            var loaded = requireChange
                ? await LoadChangeableAsync(policy.StackId, cancellationToken)
                : await LoadVisibleAsync(policy.StackId, cancellationToken);
            return (policy, loaded);
        }
        catch (NotFoundException)
        {
            // Same answer as for a missing policy, so a stack the user may not see does not leak.
            throw new NotFoundException("Image policy", policyId);
        }
    }

    private async Task<LoadedStack> LoadChangeableAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var loaded = await LoadVisibleAsync(stackId, cancellationToken);
        return loaded.CanChange
            ? loaded
            : throw new AccessDeniedException($"Image policies of stack '{loaded.Stack.Name}' are changed by its Editors.");
    }

    private async Task<LoadedStack> LoadVisibleAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        var environment = stack is null ? null : await environments.FindAsync(stack.EnvironmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (stack is null || environment is null || !AccessPolicy.CanSee(actor, environment, stack, assignments))
        {
            throw new NotFoundException("Stack", stackId);
        }

        return new LoadedStack(
            actor,
            stack,
            environment,
            AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy),
            EnvironmentAdministration.CanAdminister(actor, environment, assignments));
    }

    private sealed record LoadedStack(Actor Actor, Stack Stack, DockerEnvironment Environment, bool CanChange, bool ByAdministrator);
}
