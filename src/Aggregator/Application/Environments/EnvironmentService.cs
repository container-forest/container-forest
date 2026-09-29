using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
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
public sealed record EnvironmentSummary(
    EnvironmentId Id,
    string Name,
    bool IsPrivate,
    bool IsEnrolled,
    bool IsOnline,
    string? AgentHostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    Role? Role);

/// <summary>Environments as the current user sees them; creating environments and enrollment tokens (administrators only).</summary>
public sealed class EnvironmentService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IRoleAssignmentRepository roleAssignments,
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

    /// <summary>Environments the current user may see, in name order.</summary>
    public async Task<IReadOnlyList<EnvironmentSummary>> ListVisibleAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetAsync(cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
        var all = await environments.ListAsync(cancellationToken);

        return all
            .Where(e => AccessPolicy.CanSee(user, e, assignments))
            .Select(e => new EnvironmentSummary(
                e.Id,
                e.Name,
                e.IsPrivate,
                e.IsEnrolled,
                agents.IsOnline(e.Id),
                e.AgentHostname,
                e.AgentVersion,
                e.LastSeenAt,
                AccessPolicy.EffectiveRole(user, e, assignments)))
            .ToList();
    }

    private static EnrollmentToken IssueToken(DockerEnvironment environment, DateTimeOffset now)
    {
        var token = environment.IssueEnrollmentToken(now, DockerEnvironment.DefaultEnrollmentTokenLifetime);
        return new EnrollmentToken(token, now + DockerEnvironment.DefaultEnrollmentTokenLifetime);
    }
}
