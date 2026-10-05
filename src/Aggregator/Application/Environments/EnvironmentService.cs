using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Environments;

public sealed record EnrollmentToken(string Token, DateTimeOffset ExpiresAt);

public sealed record EnvironmentCreated(EnvironmentId Id, string Name, EnrollmentToken EnrollmentToken);

/// <param name="Role">
/// The user's role on the environment as a whole; null when the user sees it only through stack roles.
/// Administrators get null too and rely on <see cref="User.IsAdmin"/>.
/// </param>
/// <param name="EnrollmentTokenExpiresAt">
/// Until when an issued enrollment token can still be used; null when there is none. Shown to administrators only.
/// </param>
public sealed record EnvironmentSummary(
    EnvironmentId Id,
    string Name,
    bool IsPrivate,
    bool IsEnrolled,
    bool IsOnline,
    string? AgentHostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    Role? Role,
    DateTimeOffset? EnrollmentTokenExpiresAt = null);

/// <summary>
/// Environments as the current user sees them; creating, changing and removing environments, enrollment tokens and
/// revoking agents (administrators only).
/// </summary>
public sealed class EnvironmentService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IRoleAssignmentRepository roleAssignments,
    IStackRepository stacks,
    IUnitOfWork unitOfWork,
    IAgentChannel agents,
    TimeProvider time)
{
    public async Task<EnvironmentCreated> CreateAsync(string name, bool isPrivate, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var now = time.GetUtcNow();
        var environment = DockerEnvironment.Create(name, isPrivate, now);
        if (await environments.NameExistsAsync(environment.Name, cancellationToken))
        {
            throw new DomainConflictException($"Environment '{environment.Name}' already exists.");
        }

        var token = IssueToken(environment, now);
        environments.Add(environment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new EnvironmentCreated(environment.Id, environment.Name, token);
    }

    /// <summary>Issues a new enrollment token, e.g. to reinstall the agent. The previous token stops working.</summary>
    public async Task<EnrollmentToken> IssueEnrollmentTokenAsync(EnvironmentId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var environment = await environments.GetAsync(id, cancellationToken);
        var token = IssueToken(environment, time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>Renames the environment and makes it private or public.</summary>
    public async Task<EnvironmentSummary> UpdateAsync(EnvironmentId id, string name, bool isPrivate, CancellationToken cancellationToken)
    {
        var admin = await currentActor.GetAdministratorAsync(cancellationToken);
        var environment = await environments.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Environment", id);

        // Compared with the others after normalizing the name, so the environment does not clash with itself.
        var all = await environments.ListAsync(cancellationToken);
        environment.Rename(name);
        if (all.Any(e => e.Id != environment.Id && string.Equals(e.Name, environment.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"Environment '{environment.Name}' already exists.");
        }

        if (isPrivate)
        {
            environment.MakePrivate();
        }
        else
        {
            environment.MakePublic();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Summarize(admin, environment, await roleAssignments.ListForUserAsync(admin.Id, cancellationToken));
    }

    /// <summary>
    /// Forgets the agent's credentials and drops its connection: for a lost or compromised host. The agent cannot
    /// connect again until it is enrolled with a new token; containers on the host keep running.
    /// </summary>
    public async Task RevokeAgentAsync(EnvironmentId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var environment = await environments.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Environment", id);

        environment.RevokeAgent();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // After saving: a reconnect that slips in between is refused, since the secret is already gone.
        agents.Disconnect(environment.Id, "agent credentials revoked");
    }

    /// <summary>
    /// Removes an environment without stacks: its stacks must be removed first, since each has containers or files on
    /// the host and a deployment history. The database cascade takes the environment's age keys (files encrypted only
    /// for them can no longer be decrypted by the aggregator), registry logins, roles, notification rules and its place
    /// in git connections. The agent is disconnected and cannot connect again; it keeps running on the host until
    /// someone removes it there.
    /// </summary>
    /// <exception cref="NotFoundException">The environment does not exist.</exception>
    /// <exception cref="DomainConflictException">It still has stacks.</exception>
    public async Task RemoveAsync(EnvironmentId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var environment = await environments.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Environment", id);

        var stackCount = (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken)).Count;
        if (stackCount > 0)
        {
            throw new DomainConflictException(
                $"Environment '{environment.Name}' has {stackCount} stack(s); take them down and remove them first.");
        }

        environments.Remove(environment);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // After saving, as for a revoked agent: a reconnect that slips in between finds no environment.
        agents.Disconnect(environment.Id, "environment removed");
    }

    /// <summary>Environments the current user may see, in name order.</summary>
    public async Task<IReadOnlyList<EnvironmentSummary>> ListVisibleAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetAsync(cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
        var all = await environments.ListAsync(cancellationToken);

        return all
            .Where(e => AccessPolicy.CanSee(user, e, assignments))
            .Select(e => Summarize(user, e, assignments))
            .ToList();
    }

    private EnvironmentSummary Summarize(User user, DockerEnvironment environment, IReadOnlyList<RoleAssignment> assignments) => new(
        environment.Id,
        environment.Name,
        environment.IsPrivate,
        environment.IsEnrolled,
        agents.IsOnline(environment.Id),
        environment.AgentHostname,
        environment.AgentVersion,
        environment.LastSeenAt,
        AccessPolicy.EffectiveRole(user, environment, assignments),
        user.IsAdmin ? environment.EnrollmentTokenExpiresAt : null);

    private static EnrollmentToken IssueToken(DockerEnvironment environment, DateTimeOffset now)
    {
        var token = environment.IssueEnrollmentToken(now, DockerEnvironment.DefaultEnrollmentTokenLifetime);
        return new EnrollmentToken(token, now + DockerEnvironment.DefaultEnrollmentTokenLifetime);
    }
}
