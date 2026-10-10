using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Api.Contracts;

// Signing the CLI in through the browser (ADR-0008), after the device authorization flow of RFC 8628.
//
//   POST /api/cli/authorizations                       -> CliAuthorizationStartedResponse; anonymous, rate-limited
//   GET  /api/cli/authorizations/{userCode}            -> CliAuthorizationResponse; a session; 404 when unknown,
//                                                         decided or expired
//   POST /api/cli/authorizations/{userCode}/approve    -> 204; a session; the token will be the user's, with these limits
//   POST /api/cli/authorizations/{userCode}/deny       -> 204; a session
//   POST /api/cli/token                                -> CliTokenResponse; anonymous, rate-limited; the CLI polls
//
// The CLI opens the panel at VerificationPathComplete on its own server address (adding "&port=<p>" when it listens
// on 127.0.0.1:<p>, so the panel can wake it up after the decision) or shows the user code and VerificationPath.
// The token itself is created and returned only by /api/cli/token, once; the browser never sees it.

/// <param name="ClientName">What asks, e.g. "forest 0.1.0"; shown to the person, never trusted.</param>
/// <param name="HostName">The machine the CLI runs on; shown to the person, never trusted.</param>
/// <param name="Secrets">Asks for a token that may decrypt SOPS files (ADR-0010); the person decides when approving.</param>
public sealed record StartCliAuthorizationRequest(string ClientName, string HostName, bool Secrets = false);

/// <param name="DeviceCode">For the CLI only: it polls with it. Never shown.</param>
/// <param name="UserCode">Shown to the person, e.g. "WDJB-MJHT".</param>
/// <param name="VerificationPath">Panel page where the person types the user code, relative to the server address.</param>
/// <param name="VerificationPathComplete">Panel page for this request, with the code already in it.</param>
/// <param name="IntervalSeconds">How long the CLI waits between polls.</param>
public sealed record CliAuthorizationStartedResponse(
    string DeviceCode,
    string UserCode,
    string VerificationPath,
    string VerificationPathComplete,
    DateTimeOffset ExpiresAt,
    int IntervalSeconds)
{
    public override string ToString() => $"{nameof(CliAuthorizationStartedResponse)} {{ UserCode = {UserCode}, ExpiresAt = {ExpiresAt} }}";
}

/// <param name="RequestedFrom">The address the CLI's request came from, as the aggregator saw it; null when unknown.</param>
/// <param name="SecretsRequested">The CLI asked for a token that may decrypt secrets; the page suggests it, the person decides.</param>
public sealed record CliAuthorizationResponse(
    string UserCode,
    string ClientName,
    string HostName,
    string? RequestedFrom,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool SecretsRequested);

/// <summary>The limits of the token the CLI gets; the same as for <see cref="CreateApiTokenRequest"/>.</summary>
public sealed record ApproveCliAuthorizationRequest(
    string Name, bool ReadOnly, IReadOnlyList<Guid>? EnvironmentIds, DateTimeOffset? ExpiresAt, bool Secrets = false);

public sealed record CliTokenRequest(string DeviceCode)
{
    public override string ToString() => nameof(CliTokenRequest);
}

/// <param name="Status">
/// Pending: ask again after the interval. SlowDown: asked too soon; wait longer. Approved: the token is here.
/// Denied: the person said no. Expired: the request ran out, was used up or never existed; start again.
/// </param>
/// <param name="Token">The secret, only with Approved; the CLI stores it.</param>
/// <param name="UserName">Whose token it is, only with Approved.</param>
public sealed record CliTokenResponse(CliPollResult Status, string? Token, ApiTokenResponse? ApiToken, string? UserName)
{
    public override string ToString() => $"{nameof(CliTokenResponse)} {{ Status = {Status}, UserName = {UserName} }}";
}
