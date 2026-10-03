using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Repositories;

/// <summary>
/// Who may use and manage repositories and connections (ADR-0006). A user works with the repositories their
/// "editor environments" may deploy from: environments where they are an Editor of the environment or of a stack.
/// </summary>
/// <param name="Actor">The current user.</param>
/// <param name="Assignments">The actor's role assignments.</param>
/// <param name="EditorEnvironments">See <see cref="AccessPolicy.EditorEnvironments"/>; ignored for administrators.</param>
public sealed record RepositoryActor(User Actor, IReadOnlyList<RoleAssignment> Assignments, IReadOnlySet<EnvironmentId> EditorEnvironments)
{
    public bool IsAdmin => Actor.IsAdmin;

    /// <summary>Whether the user may see or add a repository with this connection (null: a public repository).</summary>
    public bool CanUse(GitConnection? connection) =>
        IsAdmin || (connection is null ? EditorEnvironments.Count > 0 : EditorEnvironments.Any(connection.IsAllowedIn));
}

/// <summary>Loads the <see cref="RepositoryActor"/> of the request and decides about single repositories.</summary>
public sealed class RepositoryAccess(
    CurrentActor currentActor,
    IRoleAssignmentRepository roleAssignments,
    IStackRepository stacks,
    IEnvironmentRepository environments)
{
    /// <exception cref="AccessDeniedException">The user is neither an administrator nor an Editor anywhere.</exception>
    public async Task<RepositoryActor> GetActorAsync(CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        var result = new RepositoryActor(actor, assignments, AccessPolicy.EditorEnvironments(actor, assignments));
        if (!result.IsAdmin && result.EditorEnvironments.Count == 0)
        {
            throw new AccessDeniedException("Only administrators and editors may work with repositories.");
        }

        return result;
    }

    /// <summary>
    /// Whether the user may change the repository or its webhook: changing it changes what its stacks deploy, so it
    /// takes the right to deploy each of them, besides being able to use its connection.
    /// </summary>
    public async Task<bool> CanManageAsync(
        RepositoryActor actor, GitRepository repository, GitConnection? connection, CancellationToken cancellationToken)
    {
        repository.EnsureOwnConnection(connection);
        if (actor.IsAdmin)
        {
            return true;
        }

        if (!actor.CanUse(connection))
        {
            return false;
        }

        var cache = new Dictionary<EnvironmentId, DockerEnvironment?>();
        foreach (var stack in await stacks.ListByRepositoryAsync(repository.Id, cancellationToken))
        {
            if (!cache.TryGetValue(stack.EnvironmentId, out var environment))
            {
                cache[stack.EnvironmentId] = environment = await environments.FindAsync(stack.EnvironmentId, cancellationToken);
            }

            if (environment is null || !AccessPolicy.CanPerform(actor.Actor, environment, stack, actor.Assignments, Permission.Deploy))
            {
                return false;
            }
        }

        return true;
    }
}
