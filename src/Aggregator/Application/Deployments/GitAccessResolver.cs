using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>How the aggregator reaches a repository with its connection's decrypted tokens (ADR-0006, ADR-0015).</summary>
public static class GitAccessResolver
{
    /// <summary>To fetch: the connection's read token, or anonymously.</summary>
    /// <param name="connection">Null for a public repository.</param>
    /// <exception cref="DeploymentFailedException">The stored token cannot be decrypted.</exception>
    public static GitAccess ForFetch(GitConnection? connection, ISecretProtector protector)
    {
        if (connection is null)
        {
            return GitAccess.Public;
        }

        return connection.ProtectedAccessToken is { } token
            ? new GitAccess(
                Credentials(connection, connection.AccessUserName, token, SecretPurpose.GitAccessToken, "access token", protector),
                IsTrustedHost: true)
            : new GitAccess(null, IsTrustedHost: true);
    }

    /// <summary>To push update commits: the connection's push token, never the read one.</summary>
    /// <exception cref="DeploymentFailedException">No push token, or it cannot be decrypted.</exception>
    public static GitAccess ForPush(GitConnection? connection, ISecretProtector protector)
    {
        if (connection?.ProtectedPushToken is not { } token)
        {
            throw new DeploymentFailedException(
                connection is null
                    ? "The repository is public, without a git connection; the aggregator pushes only through a connection's push token."
                    : $"Connection '{connection.Name}' has no push token; an administrator sets one for image updates.");
        }

        return new GitAccess(
            Credentials(connection, connection.PushUserName, token, SecretPurpose.GitPushToken, "push token", protector),
            IsTrustedHost: true);
    }

    private static GitCredentials Credentials(
        GitConnection connection, string? userName, string protectedToken, SecretPurpose purpose, string what, ISecretProtector protector)
    {
        try
        {
            return new GitCredentials(
                userName ?? GitConnection.DefaultAccessUserName, protector.Unprotect(purpose, protectedToken), connection.UrlPrefix);
        }
        catch (SecretUnreadableException)
        {
            throw new DeploymentFailedException($"The stored {what} of connection '{connection.Name}' cannot be decrypted; set it again.");
        }
    }
}
