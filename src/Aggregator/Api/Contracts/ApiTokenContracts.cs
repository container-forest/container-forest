namespace ContainerForest.Aggregator.Api.Contracts;

// Personal API tokens: the CLI and scripts send "Authorization: Bearer cf_pat_…" and act as the token's user.
//
//   GET    /api/me/tokens                     -> ApiTokenResponse[], newest first; a session only, not a token
//   POST   /api/me/tokens                     -> 201 CreatedApiTokenResponse; a session only; the token is shown once
//   DELETE /api/me/tokens/{id}                -> 204; a session only
//   DELETE /api/me/tokens/current             -> 204; revokes the token of the request itself (CLI logout)
//   GET    /api/users/{id}/tokens             -> ApiTokenResponse[]; administrators
//   DELETE /api/users/{id}/tokens/{tokenId}   -> 204; administrators
//
// A token has its user's roles, minus its limits: read-only (every POST, PUT, PATCH and DELETE is 403), some
// environments only (the others do not exist for it, and it has no administrator rights), an expiry. A token
// never manages tokens, the password or the Discord link: a limited token must not mint wider credentials.
// An unknown, revoked or expired token is 401; a deactivated user's token is 403.

/// <param name="Hint">The prefix and first characters of the token, to tell it apart, e.g. "cf_pat_x7Kq2m".</param>
/// <param name="EnvironmentIds">The environments a limited token reaches; null for all of them.</param>
/// <param name="ExpiresAt">Null when the token does not expire.</param>
/// <param name="LastUsedAt">Precise to about a minute; null when never used.</param>
public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string Hint,
    bool ReadOnly,
    IReadOnlyList<Guid>? EnvironmentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool Expired);

/// <param name="EnvironmentIds">Null for every environment; otherwise at least one environment the user can see.</param>
/// <param name="ExpiresAt">Null for a token that does not expire; otherwise in the future.</param>
public sealed record CreateApiTokenRequest(string Name, bool ReadOnly, IReadOnlyList<Guid>? EnvironmentIds, DateTimeOffset? ExpiresAt);

/// <param name="Token">The secret; only its hash is kept, so it cannot be shown again.</param>
public sealed record CreatedApiTokenResponse(ApiTokenResponse ApiToken, string Token)
{
    public override string ToString() => $"{nameof(CreatedApiTokenResponse)} {{ ApiToken = {ApiToken} }}";
}
