using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface ICliAuthorizationRepository : IRepository<CliAuthorization, CliAuthorizationId>
{
    Task<CliAuthorization?> FindByDeviceCodeHashAsync(string deviceCodeHash, CancellationToken cancellationToken);

    Task<CliAuthorization?> FindByUserCodeHashAsync(string userCodeHash, CancellationToken cancellationToken);

    Task<int> CountUnexpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Forgets requests that expired; nobody can use them any more.</summary>
    Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A started CLI login: the device code is for the CLI only, the user code is shown to the person.</summary>
public sealed record StartedCliAuthorization(CliAuthorization Authorization, string DeviceCode, string UserCode);

/// <summary>The answer to a CLI's poll; the token and the user only when it was issued.</summary>
public sealed record CliTokenResult(CliPollResult Status, ApiToken? Token, string? Secret, string? UserName);

/// <summary>
/// Signing the CLI in through the browser (ADR-0008). The CLI starts a request and polls for the token; the person
/// approves it in the panel from a session, never with a token, and chooses the token's limits there.
/// </summary>
public sealed class CliAuthorizationService(
    CurrentActor currentActor,
    ICliAuthorizationRepository authorizations,
    IApiTokenRepository tokens,
    IUserRepository users,
    ApiTokenService apiTokens,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>
    /// Requests open at the same time across the system. The endpoints are rate-limited per address; this caps the
    /// table when many addresses start requests.
    /// </summary>
    public const int MaxOpenRequests = 500;

    /// <param name="requestedFrom">The client's address as the aggregator sees it; shown to the person who approves.</param>
    /// <param name="secretsRequested">The CLI asks for a token that may decrypt secrets; the person decides.</param>
    /// <exception cref="DomainConflictException">Too many requests are open.</exception>
    public async Task<StartedCliAuthorization> StartAsync(
        string clientName, string hostName, string? requestedFrom, CancellationToken cancellationToken, bool secretsRequested = false)
    {
        var now = time.GetUtcNow();
        await authorizations.RemoveExpiredAsync(now, cancellationToken);
        if (await authorizations.CountUnexpiredAsync(now, cancellationToken) >= MaxOpenRequests)
        {
            throw new DomainConflictException("Too many CLI logins are waiting; try again in a few minutes.");
        }

        var (authorization, deviceCode, userCode) = CliAuthorization.Start(clientName, hostName, requestedFrom, now, secretsRequested);
        authorizations.Add(authorization);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new StartedCliAuthorization(authorization, deviceCode, CliAuthorization.FormatUserCode(userCode));
    }

    /// <summary>The request behind a user code, for the person to check before approving.</summary>
    /// <exception cref="NotFoundException">No open request has this code.</exception>
    public async Task<CliAuthorization> GetOpenAsync(string userCode, CancellationToken cancellationToken)
    {
        await currentActor.GetSignedInUserAsync(cancellationToken);
        return await FindOpenAsync(userCode, cancellationToken);
    }

    /// <summary>Approves the request: the CLI gets a token of the current user with these limits.</summary>
    /// <param name="environmentIds">Null for every environment; otherwise environments the user can see.</param>
    /// <param name="canDecryptSecrets">The token may have the aggregator decrypt SOPS files (ADR-0010).</param>
    /// <exception cref="NotFoundException">No open request has this code, or an environment is unknown.</exception>
    public async Task ApproveAsync(
        string userCode,
        string tokenName,
        bool readOnly,
        IReadOnlyCollection<EnvironmentId>? environmentIds,
        DateTimeOffset? tokenExpiresAt,
        CancellationToken cancellationToken,
        bool canDecryptSecrets = false)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        var authorization = await FindOpenAsync(userCode, cancellationToken);
        await apiTokens.EnsureRoomForTokenAsync(user.Id, cancellationToken);
        await apiTokens.EnsureEnvironmentsVisibleAsync(user, environmentIds, cancellationToken);

        authorization.Approve(user.Id, tokenName, readOnly, environmentIds, tokenExpiresAt, time.GetUtcNow(), canDecryptSecrets);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <exception cref="NotFoundException">No open request has this code.</exception>
    public async Task DenyAsync(string userCode, CancellationToken cancellationToken)
    {
        await currentActor.GetSignedInUserAsync(cancellationToken);
        var authorization = await FindOpenAsync(userCode, cancellationToken);
        authorization.Deny(time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The CLI polls with its device code. Once the request is approved, the token is created and returned, and the
    /// request is forgotten: the token is handed out once. An unknown device code is answered as an expired one.
    /// </summary>
    public async Task<CliTokenResult> ExchangeAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (await authorizations.FindByDeviceCodeHashAsync(CliAuthorization.HashDeviceCode(deviceCode), cancellationToken)
            is not { } authorization)
        {
            return new CliTokenResult(CliPollResult.Expired, null, null, null);
        }

        var status = authorization.Poll(now);
        switch (status)
        {
            case CliPollResult.Approved:
                return await IssueAsync(authorization, now, cancellationToken);
            case CliPollResult.Denied or CliPollResult.Expired:
                authorizations.Remove(authorization);
                break;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CliTokenResult(status, null, null, null);
    }

    private async Task<CliTokenResult> IssueAsync(CliAuthorization authorization, DateTimeOffset now, CancellationToken cancellationToken)
    {
        authorizations.Remove(authorization);

        // The user could have been deactivated, or filled their tokens, since approving.
        var user = await users.FindAsync(authorization.ApprovedBy!.Value, cancellationToken);
        if (user is not { IsActive: true }
            || await tokens.CountForUserAsync(user.Id, cancellationToken) >= ApiTokenService.MaxTokensPerUser)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new CliTokenResult(CliPollResult.Denied, null, null, null);
        }

        var (token, secret) = authorization.IssueToken(now);
        tokens.Add(token);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CliTokenResult(CliPollResult.Approved, token, secret, user.UserName);
    }

    private async Task<CliAuthorization> FindOpenAsync(string userCode, CancellationToken cancellationToken)
    {
        var authorization = await authorizations.FindByUserCodeHashAsync(CliAuthorization.HashUserCode(userCode), cancellationToken);
        return authorization is not null && authorization.IsOpen(time.GetUtcNow())
            ? authorization
            : throw new NotFoundException(nameof(CliAuthorization), CliAuthorization.FormatUserCode(userCode));
    }
}
