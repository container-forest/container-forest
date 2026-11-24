using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <param name="RepositoryName">Null when the repository is gone.</param>
/// <param name="CanDeploy">Whether the current user may deploy the stack, change its source or take it down; the panel uses it to show the buttons.</param>
/// <param name="DeployOnPush">A push to the branch deploys the stack, when the repository has a webhook.</param>
/// <param name="RestartOnFileChange">A deployment restarts the stack when its files other than the compose file changed.</param>
/// <param name="HasDeployHook">CI can deploy the stack with fresh images through its deploy hook.</param>
/// <param name="RequireSignedCommits">Only content endorsed by a signature or an approval is deployed (ADR-0011).</param>
/// <param name="RequireImageDigests">Every image must be pinned by a digest.</param>
/// <param name="CanRestart">Whether the current user may restart the stack.</param>
/// <param name="CanRemove">Whether the current user may remove the stack: an Editor of its environment, as for adding one.</param>
/// <param name="PolicyAllowances">Exceptions to the environment's compose policy for this stack (ADR-0012).</param>
/// <param name="CanAdministerEnvironment">Whether the current user may change the stack's allowances.</param>
/// <param name="PolicyUnrestricted">The compose policy does not limit the stack (ADR-0012).</param>
/// <param name="HasImageHook">CI can ask for the stack's image policies to be checked (ADR-0015).</param>
/// <param name="ImageHookAllowsDowngrade">The image hook may ask for an older tag, which pins the image.</param>
public sealed record StackSummary(
    StackId Id,
    EnvironmentId EnvironmentId,
    StackName Name,
    RepositoryId RepositoryId,
    string? RepositoryName,
    BranchName Branch,
    StackFilePath ComposePath,
    bool IsSystem,
    CommitSha? DeployedCommit,
    DateTimeOffset? DeployedAt,
    bool DeployOnPush,
    bool RestartOnFileChange,
    bool HasDeployHook,
    bool RequireSignedCommits,
    bool RequireImageDigests,
    bool CanDeploy,
    bool CanRestart,
    bool CanRemove,
    IReadOnlyList<string> PolicyAllowances,
    bool CanAdministerEnvironment,
    bool PolicyUnrestricted = false,
    bool HasImageHook = false,
    bool ImageHookAllowsDowngrade = false,
    CommitSha? HeldCommit = null,
    DateTimeOffset? HeldSince = null);

/// <summary>
/// Stacks as the current user sees them; adding and removing stacks (Editor on the environment) and changing their
/// source (Editor on the stack).
/// </summary>
public sealed class StackService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IGitRepositoryRepository repositories,
    IGitConnectionRepository connections,
    IRoleAssignmentRepository roleAssignments,
    IDeploymentRepository deployments,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    /// <summary>Adds a stack. It is deployed separately, see <c>DeploymentService</c>.</summary>
    public Task<StackSummary> CreateAsync(
        EnvironmentId environmentId,
        string name,
        RepositoryId repositoryId,
        string branch,
        string composePath,
        CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackCreated, new StackAuditTarget(null, name.Trim(), environmentId)) { Details = $"{branch.Trim()} · {composePath.Trim()}" };
        return audit.RunAsync(draft, async () =>
        {
            var actor = await currentActor.GetAsync(cancellationToken);
            var environment = await environments.FindAsync(environmentId, cancellationToken);
            var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
            if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
            {
                throw new NotFoundException("Environment", environmentId);
            }

            // A stack's compose file can mount anything on the host, so adding one takes the same role as deploying.
            if (!AccessPolicy.CanPerform(actor, environment, assignments, Permission.Deploy))
            {
                throw new AccessDeniedException($"Adding stacks to environment '{environment.Name}' requires the Editor role on it.");
            }

            var stackName = StackName.Parse(name);
            var repository = await UsableRepositoryAsync(repositoryId, environment, cancellationToken);
            var stack = Stack.Create(
                environment.Id,
                stackName,
                repository.Id,
                BranchName.Parse(branch),
                StackFilePath.Parse(composePath),
                isSystem: false,
                time.GetUtcNow());
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);

            if (await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken) is not null)
            {
                throw new DomainConflictException($"Environment '{environment.Name}' already has a stack '{stackName}'.");
            }

            // Container names belong to the stack with the longest matching name (ADR-0012): a new stack must not take
            // over a name another stack's deployed container already has.
            foreach (var other in await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken))
            {
                var names = other.DeployedComposeFacts is { } json
                    ? Policies.ComposeFactsReader.Deserialize(json)?.Services.Select(s => s.ContainerName).OfType<string>() ?? []
                    : [];
                if (names.FirstOrDefault(n => n == stackName.Value || n.StartsWith(stackName.Value + "-", StringComparison.Ordinal)) is { } taken)
                {
                    throw new DomainConflictException(
                        $"Stack '{other.Name}' has a container named '{taken}', which would belong to a stack '{stackName}'; choose another name.");
                }
            }

            stacks.Add(stack);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>Stacks of the environment the current user may see, in name order.</summary>
    public async Task<IReadOnlyList<StackSummary>> ListVisibleAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var repositoryNames = await RepositoryNamesAsync(cancellationToken);
        return (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken))
            .Where(s => AccessPolicy.CanSee(actor, environment, s, assignments))
            .OrderBy(s => s.Name.Value, StringComparer.Ordinal)
            .Select(s => Summarize(actor, environment, s, assignments, repositoryNames))
            .ToList();
    }

    /// <exception cref="NotFoundException">The stack does not exist or the user may not see it.</exception>
    public async Task<StackSummary> GetAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
        return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
    }

    /// <summary>
    /// Points the stack at another repository, branch or compose file; the next deployment uses them. The stack's
    /// name stays: it is the compose project and the directory on the host.
    /// </summary>
    public Task<StackSummary> ChangeSourceAsync(
        StackId stackId, RepositoryId repositoryId, string branch, string composePath, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackSourceChanged) { Details = $"{branch.Trim()} · {composePath.Trim()}" };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            if (!AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy))
            {
                throw new AccessDeniedException($"Changing stack '{stack.Name}' requires the Editor role.");
            }

            var branchName = BranchName.Parse(branch);
            var path = StackFilePath.Parse(composePath);
            var repository = await UsableRepositoryAsync(repositoryId, environment, cancellationToken);

            // A queued deployment reads the source when it runs; changing it underneath would deploy something nobody asked for.
            if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
            {
                throw new DomainConflictException($"Stack '{stack.Name}' has a deployment in progress; change it after that.");
            }

            stack.ChangeSource(repository.Id, branchName, path);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Turns deploying on push on or off; the same role as deploying, since every push then deploys. Allowed during
    /// a deployment: it only decides about later pushes.
    /// </summary>
    public async Task<StackSummary> SetDeployOnPushAsync(StackId stackId, bool enabled, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
        if (!AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy))
        {
            throw new AccessDeniedException($"Changing stack '{stack.Name}' requires the Editor role.");
        }

        stack.SetDeployOnPush(enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
    }

    /// <summary>
    /// Turns restarting after a deployment that changed the stack's files on or off; the same role as deploying.
    /// Takes effect from the next deployment.
    /// </summary>
    public async Task<StackSummary> SetRestartOnFileChangeAsync(StackId stackId, bool enabled, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
        stack.SetRestartOnFileChange(enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
    }

    /// <summary>
    /// Turns requiring endorsed commits on or off (ADR-0011); the same role as deploying. Recorded in the audit log:
    /// turning it off lets anyone who can push deploy again. A queued deployment checks it when it runs.
    /// </summary>
    public Task<StackSummary> SetRequireSignedCommitsAsync(StackId stackId, bool enabled, CancellationToken cancellationToken) =>
        SetEndorsementAsync(stackId, enabled ? "Require signed commits: on" : "Require signed commits: off",
            stack => stack.SetRequireSignedCommits(enabled), cancellationToken);

    /// <summary>Turns requiring images pinned by digest on or off; the same role as deploying, recorded in the audit log.</summary>
    public Task<StackSummary> SetRequireImageDigestsAsync(StackId stackId, bool enabled, CancellationToken cancellationToken) =>
        SetEndorsementAsync(stackId, enabled ? "Require image digests: on" : "Require image digests: off",
            stack => stack.SetRequireImageDigests(enabled), cancellationToken);

    /// <summary>
    /// Ends the hold of a rollback (<see cref="Stack.ReleaseHold"/>); the same role as deploying, recorded in the audit
    /// log. Nothing is deployed now: the next push, deploy hook call or image update deploys the branch head.
    /// </summary>
    public Task<StackSummary> ReleaseHoldAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackHoldReleased);
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            draft.Details = stack.HeldCommit is { } held ? $"Held at {held.Short}" : "Not held";
            stack.ReleaseHold();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    private Task<StackSummary> SetEndorsementAsync(
        StackId stackId, string details, Action<Stack> change, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackEndorsementChanged) { Details = details };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            change(stack);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Sets the stack's own exceptions to the compose policy (ADR-0012). Administrators of the system or of the
    /// stack's environment only: an allowance can hand the stack root on the host.
    /// </summary>
    /// <exception cref="DomainValidationException">An allowance is not valid.</exception>
    public Task<StackSummary> SetPolicyAllowancesAsync(StackId stackId, IReadOnlyList<string> allowances, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackPolicyAllowancesChanged) { Details = Describe(allowances) };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            if (!EnvironmentAdministration.CanAdminister(actor, environment, assignments))
            {
                throw new AccessDeniedException($"Only administrators of environment '{environment.Name}' give stacks policy allowances.");
            }

            var before = stack.PolicyAllowances;
            stack.SetPolicyAllowances(allowances);
            draft.Details = $"{Describe(before)} → {Describe(stack.PolicyAllowances)}";
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Takes the stack out of the compose policy, or puts it back (ADR-0012). For administrators of the system or of the
    /// environment: an unrestricted stack's Editors are root on the host.
    /// </summary>
    public Task<StackSummary> SetPolicyUnrestrictedAsync(StackId stackId, bool unrestricted, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackPolicyUnrestrictedChanged) { Details = unrestricted ? "Unrestricted" : "Restricted" };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            if (!EnvironmentAdministration.CanAdminister(actor, environment, assignments))
            {
                throw new AccessDeniedException($"Only administrators of environment '{environment.Name}' take stacks out of the compose policy.");
            }

            stack.SetPolicyUnrestricted(unrestricted);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    private static string Describe(IReadOnlyList<string> allowances) => allowances.Count == 0 ? "none" : string.Join(", ", allowances);

    /// <summary>
    /// Creates the stack's deploy hook, or replaces its token. The token is returned once and only its hash is kept.
    /// The same role as deploying, since every call then deploys.
    /// </summary>
    public Task<(StackSummary Stack, string Token)> CreateDeployHookAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.DeployHookCreated);
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            var token = SecretToken.Generate();
            stack.SetDeployHook(SecretToken.Hash(token));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return (Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken)), token);
        }, cancellationToken);
    }

    public Task<StackSummary> RemoveDeployHookAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.DeployHookRemoved);
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            stack.RemoveDeployHook();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Creates the stack's image hook (ADR-0015), or replaces its token. The token is returned once and only its hash is
    /// kept. The same role as deploying: every call may commit and deploy a new tag within the stack's image policies.
    /// </summary>
    public Task<(StackSummary Stack, string Token)> CreateImageHookAsync(StackId stackId, bool allowDowngrade, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImageHookCreated) { Details = allowDowngrade ? "Downgrades allowed" : "No downgrades" };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            var token = SecretToken.Generate();
            stack.SetImageHook(SecretToken.Hash(token), allowDowngrade);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return (Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken)), token);
        }, cancellationToken);
    }

    /// <summary>Lets the image hook roll back (and pin) or stops it, keeping its token.</summary>
    public Task<StackSummary> SetImageHookDowngradeAsync(StackId stackId, bool allowDowngrade, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImageHookCreated) { Details = allowDowngrade ? "Downgrades allowed" : "No downgrades" };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            stack.SetImageHookAllowsDowngrade(allowDowngrade);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    public Task<StackSummary> RemoveImageHookAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ImageHookRemoved);
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            stack.RemoveImageHook();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Removes the stack with its deployment history, the roles on it and the notification rules about it (the
    /// database cascades). Only a stack without containers of its own: one never deployed or taken down. Its files
    /// stay on the host, as after a down; a stack added later under the same name deploys over them.
    /// </summary>
    /// <exception cref="AccessDeniedException">The user is not an Editor of the environment.</exception>
    /// <exception cref="DomainConflictException">A system stack, a deployed one, or one with a deployment in progress.</exception>
    public Task RemoveAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.StackRemoved);
        return audit.RunAsync(draft, async () =>
        {
            var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
            draft.Target = new StackAuditTarget(stack.Id, stack.Name.Value, stack.EnvironmentId);
            if (!CanRemove(actor, environment, assignments))
            {
                throw new AccessDeniedException($"Removing stack '{stack.Name}' requires the Editor role on environment '{environment.Name}'.");
            }

            stack.EnsureRemovable();

            // The worker would deploy a stack that is no longer there, and the deployment's row would block the delete.
            if (await deployments.HasUnfinishedAsync(stack.Id, cancellationToken))
            {
                throw new DomainConflictException($"Stack '{stack.Name}' has a deployment in progress; remove it after that.");
            }

            stacks.Remove(stack);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    // The role that adds stacks: a stack's own Editor did not add it and may not drop the roles others have on it.
    private static bool CanRemove(Actor actor, DockerEnvironment environment, IReadOnlyList<RoleAssignment> assignments) =>
        AccessPolicy.CanPerform(actor, environment, assignments, RolePermissions.RequiredFor(StackOperation.Remove));

    private async Task<(Actor Actor, Stack Stack, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments)> LoadDeployableAsync(
        StackId stackId, CancellationToken cancellationToken)
    {
        var loaded = await LoadVisibleAsync(stackId, cancellationToken);
        if (!AccessPolicy.CanPerform(loaded.Actor, loaded.Environment, loaded.Stack, loaded.Assignments, Permission.Deploy))
        {
            throw new AccessDeniedException($"Changing stack '{loaded.Stack.Name}' requires the Editor role.");
        }

        return loaded;
    }

    private static StackSummary Summarize(
        Actor actor,
        DockerEnvironment environment,
        Stack stack,
        IReadOnlyList<RoleAssignment> assignments,
        IReadOnlyDictionary<RepositoryId, string> repositoryNames) => new(
        stack.Id,
        stack.EnvironmentId,
        stack.Name,
        stack.RepositoryId,
        repositoryNames.GetValueOrDefault(stack.RepositoryId),
        stack.Branch,
        stack.ComposePath,
        stack.IsSystem,
        stack.DeployedCommit,
        stack.DeployedAt,
        stack.DeployOnPush,
        stack.RestartOnFileChange,
        stack.HasDeployHook,
        stack.RequireSignedCommits,
        stack.RequireImageDigests,
        AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy),
        AccessPolicy.CanPerform(actor, environment, stack, assignments, StackOperation.Restart),
        CanRemove(actor, environment, assignments) && !stack.IsSystem,
        stack.PolicyAllowances,
        EnvironmentAdministration.CanAdminister(actor, environment, assignments),
        stack.PolicyUnrestricted,
        stack.HasImageHook,
        stack.ImageHookAllowsDowngrade,
        stack.HeldCommit,
        stack.HeldSince);

    /// <summary>
    /// The repository, if stacks of the environment may deploy from it (ADR-0006). One that may not is reported as
    /// missing, like the repositories of connections the user does not see.
    /// </summary>
    private async Task<GitRepository> UsableRepositoryAsync(
        RepositoryId repositoryId, DockerEnvironment environment, CancellationToken cancellationToken)
    {
        var repository = await repositories.FindAsync(repositoryId, cancellationToken);
        var connection = repository?.ConnectionId is { } connectionId ? await connections.FindAsync(connectionId, cancellationToken) : null;
        if (repository is null || (repository.ConnectionId is not null && connection is null) || !repository.IsUsableIn(environment.Id, connection))
        {
            throw new NotFoundException("Repository", repositoryId);
        }

        return repository;
    }

    // Names only: the list of repositories itself is for administrators and editors (RepositoryService).
    private async Task<IReadOnlyDictionary<RepositoryId, string>> RepositoryNamesAsync(CancellationToken cancellationToken) =>
        (await repositories.ListAsync(cancellationToken)).ToDictionary(r => r.Id, r => r.Name);

    /// <exception cref="NotFoundException">The stack does not exist or the user may not see it.</exception>
    private async Task<(Actor Actor, Stack Stack, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments)> LoadVisibleAsync(
        StackId stackId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        var environment = stack is null ? null : await environments.FindAsync(stack.EnvironmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        // Reported as missing, so the existence of a stack the user may not see does not leak.
        if (stack is null || environment is null || !AccessPolicy.CanSee(actor, environment, stack, assignments))
        {
            throw new NotFoundException("Stack", stackId);
        }

        return (actor, stack, environment, assignments);
    }
}
