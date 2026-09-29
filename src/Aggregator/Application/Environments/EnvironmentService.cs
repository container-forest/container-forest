using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Environments;

public sealed record EnrollmentToken(string Token, DateTimeOffset ExpiresAt);

public sealed record EnvironmentCreated(EnvironmentId Id, string Name, EnrollmentToken EnrollmentToken);

public sealed record EnvironmentSummary(
    EnvironmentId Id,
    string Name,
    bool IsPrivate,
    bool IsEnrolled,
    bool IsOnline,
    string? AgentHostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt);

/// <summary>Creating environments and issuing enrollment tokens for their agents.</summary>
public sealed class EnvironmentService(
    IEnvironmentRepository environments,
    IUnitOfWork unitOfWork,
    IAgentChannel agents,
    TimeProvider time)
{
    public async Task<EnvironmentCreated> CreateAsync(string name, bool isPrivate, CancellationToken cancellationToken)
    {
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
        var environment = await environments.GetAsync(id, cancellationToken);
        var token = IssueToken(environment, time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<IReadOnlyList<EnvironmentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await environments.ListAsync(cancellationToken);
        return all
            .Select(e => new EnvironmentSummary(
                e.Id, e.Name, e.IsPrivate, e.IsEnrolled, agents.IsOnline(e.Id), e.AgentHostname, e.AgentVersion, e.LastSeenAt))
            .ToList();
    }

    private static EnrollmentToken IssueToken(DockerEnvironment environment, DateTimeOffset now)
    {
        var token = environment.IssueEnrollmentToken(now, DockerEnvironment.DefaultEnrollmentTokenLifetime);
        return new EnrollmentToken(token, now + DockerEnvironment.DefaultEnrollmentTokenLifetime);
    }
}
