using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Agents;

public sealed record AgentCredentials(EnvironmentId EnvironmentId, string Secret);

/// <summary>Agent enrollment, authentication and the connection bookkeeping stored with the environment.</summary>
public sealed class AgentEnrollmentService(IEnvironmentRepository environments, IUnitOfWork unitOfWork, TimeProvider time)
{
    /// <summary>Returns null when the token is unknown, expired or already used.</summary>
    public async Task<AgentCredentials?> EnrollAsync(string enrollmentToken, string hostname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(enrollmentToken))
        {
            return null;
        }

        var environment = await environments.FindByEnrollmentTokenHashAsync(SecretToken.Hash(enrollmentToken), cancellationToken);
        if (environment is null || !environment.TryEnroll(enrollmentToken, hostname, time.GetUtcNow(), out var secret))
        {
            return null;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new AgentCredentials(environment.Id, secret);
    }

    /// <summary>
    /// Returns the environment the credentials belong to, or null when they are wrong.
    /// This is the only way a connection gets its environment (security invariant 3).
    /// </summary>
    public async Task<EnvironmentId?> AuthenticateAsync(EnvironmentId agentId, string secret, CancellationToken cancellationToken)
    {
        var environment = await environments.FindAsync(agentId, cancellationToken);
        return environment is not null && environment.VerifyAgentSecret(secret) ? environment.Id : null;
    }

    /// <summary>Records the agent's Hello. Returns true when the host rebooted and file secrets must be re-sent.</summary>
    public async Task<bool> RecordConnectedAsync(
        EnvironmentId environmentId, string bootId, string agentVersion, CancellationToken cancellationToken)
    {
        var environment = await environments.GetAsync(environmentId, cancellationToken);
        var rebooted = environment.RecordConnection(bootId, agentVersion, time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return rebooted;
    }

    public async Task RecordDisconnectedAsync(
        EnvironmentId environmentId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        if (environment is null)
        {
            return;
        }

        environment.RecordHeartbeat(lastSeenAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
