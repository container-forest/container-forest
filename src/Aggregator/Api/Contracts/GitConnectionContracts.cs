namespace ContainerForest.Aggregator.Api.Contracts;

// Connections to git servers (ADR-0006): a URL prefix, the token the aggregator fetches with, and the environments
// that may deploy from the repositories under the prefix. Administrators manage them; editors list the ones allowed
// in their environments, to add repositories under them.
//
//   GET    /api/git-connections                    -> GitConnectionResponse[] (name order); administrators and editors
//   POST   /api/git-connections                    -> 201 GitConnectionResponse; administrators
//   PUT    /api/git-connections/{id}               -> GitConnectionResponse; administrators; 409 when a repository would
//                                                     be left outside the new prefix; 400 for another host while a token is set
//   DELETE /api/git-connections/{id}               -> 204; administrators; 409 while repositories use it
//   PUT    /api/git-connections/{id}/access-token  -> GitConnectionResponse; administrators; sets or replaces the token
//   DELETE /api/git-connections/{id}/access-token  -> GitConnectionResponse; administrators; anonymous access from now on
//   POST   /api/git-connections/{id}/webhook       -> GitConnectionWebhookSecretResponse; administrators; for an
//                                                     organization-wide webhook; a new secret, the old one stops working
//   DELETE /api/git-connections/{id}/webhook       -> GitConnectionResponse; administrators
//
// The token is write-only: responses only say whether one is set. The webhook secret is shown once. The git server
// sends deliveries to POST /api/webhooks/connections/{connectionId} (WebhookContracts).

/// <param name="UrlPrefix">Repositories of the connection have URLs under it; the token is sent only there.</param>
/// <param name="AccessUserName">User name sent with the token; null for the default or without a token.</param>
/// <param name="AllEnvironments">Every environment may deploy from the connection's repositories.</param>
/// <param name="EnvironmentIds">
/// The environments that may, when not <paramref name="AllEnvironments"/>; for editors, only their own.
/// </param>
/// <param name="RepositoryCount">How many repositories use the connection.</param>
public sealed record GitConnectionResponse(
    Guid Id,
    string Name,
    string UrlPrefix,
    bool HasAccessToken,
    string? AccessUserName,
    bool AllEnvironments,
    IReadOnlyList<Guid> EnvironmentIds,
    bool HasWebhook,
    int RepositoryCount,
    DateTimeOffset CreatedAt);

/// <param name="AccessToken">Null for anonymous access.</param>
/// <param name="EnvironmentIds">Ignored when <paramref name="AllEnvironments"/>.</param>
public sealed record CreateGitConnectionRequest(
    string Name, string UrlPrefix, GitAccessTokenRequest? AccessToken, bool AllEnvironments, IReadOnlyList<Guid>? EnvironmentIds);

/// <param name="EnvironmentIds">Ignored when <paramref name="AllEnvironments"/>.</param>
public sealed record UpdateGitConnectionRequest(string Name, string UrlPrefix, bool AllEnvironments, IReadOnlyList<Guid>? EnvironmentIds);

/// <param name="UserName">Null for "x-access-token", which GitHub, Gitea and GitLab accept with any token; GitLab deploy
/// tokens and Bitbucket need their own.</param>
public sealed record GitAccessTokenRequest(string? UserName, string Token)
{
    public override string ToString() => $"{nameof(GitAccessTokenRequest)} {{ UserName = {UserName} }}";
}

/// <param name="Secret">Shown only in this response; the git server signs deliveries with it.</param>
public sealed record GitConnectionWebhookSecretResponse(GitConnectionResponse Connection, string Secret)
{
    public override string ToString() => $"{nameof(GitConnectionWebhookSecretResponse)} {{ Connection = {Connection} }}";
}
