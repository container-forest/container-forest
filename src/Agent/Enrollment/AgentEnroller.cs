using ContainerForest.Agent.Configuration;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Enrollment;

/// <summary>Loads the saved credentials or, on the first start, exchanges the enrollment token for them.</summary>
public sealed class AgentEnroller(CredentialStore store, IOptions<AgentOptions> options, ILogger<AgentEnroller> logger)
{
    /// <exception cref="FatalAgentException">No way to obtain credentials without an administrator.</exception>
    /// <exception cref="RpcException">The aggregator is unreachable; worth retrying.</exception>
    public async Task<AgentCredentials> GetCredentialsAsync(AgentGateway.AgentGatewayClient gateway, CancellationToken cancellationToken)
    {
        if (await store.LoadAsync(cancellationToken) is { } saved)
        {
            return saved;
        }

        var token = options.Value.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new FatalAgentException(
                $"The agent is not enrolled: {store.FilePath} does not exist and CF_AGENT__ENROLLMENTTOKEN is not set.");
        }

        EnrollResponse response;
        try
        {
            response = await gateway.EnrollAsync(
                new EnrollRequest { EnrollmentToken = token.Trim(), Hostname = Environment.MachineName, AgentVersion = AgentInfo.Version },
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            throw new FatalAgentException(
                $"The aggregator rejected the enrollment token ({ex.Status.Detail}). Issue a new token for this environment.", ex);
        }

        var credentials = new AgentCredentials(response.AgentId, response.AgentSecret);
        try
        {
            await store.SaveAsync(credentials, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The token is already spent, so a retry with the same token would fail.
            throw new FatalAgentException(
                $"Enrolled as agent {credentials.AgentId}, but could not save the credentials to {store.FilePath}: {ex.Message} "
                + "Fix the directory permissions and enroll again with a new token.",
                ex);
        }

        logger.LogInformation(
            "Enrolled as agent {AgentId} on host {Hostname}; credentials saved to {Path}",
            credentials.AgentId, Environment.MachineName, store.FilePath);
        return credentials;
    }
}
