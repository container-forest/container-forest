using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <param name="CanDeploy">Whether the current user may deploy the stack or take it down; the panel uses it to show the buttons.</param>
/// <param name="CanRestart">Whether the current user may restart the stack.</param>
public sealed record StackSummary(
    StackId Id,
    StackName Name,
    RepositoryId RepositoryId,
    BranchName Branch,
    StackFilePath ComposePath,
    bool IsSystem,
    CommitSha? DeployedCommit,
    DateTimeOffset? DeployedAt,
    bool CanDeploy,
    bool CanRestart);

/// <summary>Stacks of an environment as the current user sees them; adding stacks (Editor on the environment).</summary>
public sealed class StackService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IGitRepositoryRepository repositories,
    IRoleAssignmentRepository roleAssignments,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Adds a stack. It is deployed separately, see <c>DeploymentService</c>.</summary>
    public async Task<Stack> CreateAsync(
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
        var repository = await repositories.GetAsync(repositoryId, cancellationToken);
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
        return stack;
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

        return (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken))
            .Where(s => AccessPolicy.CanSee(actor, environment, s, assignments))
            .OrderBy(s => s.Name.Value, StringComparer.Ordinal)
            .Select(s => new StackSummary(
                s.Id,
                s.Name,
                s.RepositoryId,
                s.Branch,
                s.ComposePath,
                s.IsSystem,
                s.DeployedCommit,
                s.DeployedAt,
                AccessPolicy.CanPerform(actor, environment, s, assignments, Permission.Deploy),
                AccessPolicy.CanPerform(actor, environment, s, assignments, StackOperation.Restart)))
            .ToList();
    }
}
