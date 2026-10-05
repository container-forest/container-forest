using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
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
/// <param name="CanRestart">Whether the current user may restart the stack.</param>
/// <param name="CanRemove">Whether the current user may remove the stack: an Editor of its environment, as for adding one.</param>
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
    bool CanDeploy,
    bool CanRestart,
    bool CanRemove);

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
    TimeProvider time)
{
    /// <summary>Adds a stack. It is deployed separately, see <c>DeploymentService</c>.</summary>
    public async Task<StackSummary> CreateAsync(
        EnvironmentId environmentId,
        string name,
        RepositoryId repositoryId,
        string branch,
        string composePath,
        CancellationToken cancellationToken)
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

        if (await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken) is not null)
        {
            throw new DomainConflictException($"Environment '{environment.Name}' already has a stack '{stackName}'.");
        }

        stacks.Add(stack);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
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
    public async Task<StackSummary> ChangeSourceAsync(
        StackId stackId, RepositoryId repositoryId, string branch, string composePath, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
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
    /// Creates the stack's deploy hook, or replaces its token. The token is returned once and only its hash is kept.
    /// The same role as deploying, since every call then deploys.
    /// </summary>
    public async Task<(StackSummary Stack, string Token)> CreateDeployHookAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
        var token = SecretToken.Generate();
        stack.SetDeployHook(SecretToken.Hash(token));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken)), token);
    }

    public async Task<StackSummary> RemoveDeployHookAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadDeployableAsync(stackId, cancellationToken);
        stack.RemoveDeployHook();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Summarize(actor, environment, stack, assignments, await RepositoryNamesAsync(cancellationToken));
    }

    /// <summary>
    /// Removes the stack with its deployment history, the roles on it and the notification rules about it (the
    /// database cascades). Only a stack without containers of its own: one never deployed or taken down. Its files
    /// stay on the host, as after a down; a stack added later under the same name deploys over them.
    /// </summary>
    /// <exception cref="AccessDeniedException">The user is not an Editor of the environment.</exception>
    /// <exception cref="DomainConflictException">A system stack, a deployed one, or one with a deployment in progress.</exception>
    public async Task RemoveAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var (actor, stack, environment, assignments) = await LoadVisibleAsync(stackId, cancellationToken);
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
    }

    // The role that adds stacks: a stack's own Editor did not add it and may not drop the roles others have on it.
    private static bool CanRemove(User actor, DockerEnvironment environment, IReadOnlyList<RoleAssignment> assignments) =>
        AccessPolicy.CanPerform(actor, environment, assignments, RolePermissions.RequiredFor(StackOperation.Remove));

    private async Task<(User Actor, Stack Stack, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments)> LoadDeployableAsync(
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
        User actor,
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
        AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy),
        AccessPolicy.CanPerform(actor, environment, stack, assignments, StackOperation.Restart),
        CanRemove(actor, environment, assignments) && !stack.IsSystem);

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
    private async Task<(User Actor, Stack Stack, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments)> LoadVisibleAsync(
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
