using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>Finds the API token a request came with. The bearer authentication of the API calls it on every request.</summary>
public sealed class ApiTokenAuthenticator(IApiTokenRepository tokens, IUnitOfWork unitOfWork, TimeProvider time)
{
    /// <summary>The token with this secret, or null when there is none or it has expired.</summary>
    /// <remarks>Whether its user is still active, <see cref="CurrentActor"/> decides, as for a session.</remarks>
    public async Task<ApiToken?> AuthenticateAsync(string secret, CancellationToken cancellationToken)
    {
        if (!ApiToken.HasTokenShape(secret))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (await tokens.FindByHashAsync(ApiToken.HashSecret(secret), cancellationToken) is not { } token || token.IsExpired(now))
        {
            return null;
        }

        if (token.RecordUse(now))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return token;
    }
}
