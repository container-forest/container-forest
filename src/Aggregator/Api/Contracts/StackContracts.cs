using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Api.Contracts;

// Repositories, stacks and deployments for the panel, limited to what the signed-in user may see and do.
//
//   GET  /api/repositories                       -> RepositoryResponse[] (name order); administrators and editors
//   POST /api/repositories                       -> 201 RepositoryResponse; administrators only
//   PUT  /api/repositories/{id}                  -> RepositoryResponse; administrators; 400 for another host while a token is set
//   PUT  /api/repositories/{id}/access-token     -> RepositoryResponse; administrators; sets or replaces the token
//   DELETE /api/repositories/{id}/access-token   -> RepositoryResponse; administrators; the repository becomes public
//   GET  /api/environments/{id}/stacks           -> StackResponse[] (name order)
//   POST /api/environments/{id}/stacks           -> 201 StackResponse; Editor on the environment
//   GET  /api/stacks/{id}                        -> StackResponse
//   PUT  /api/stacks/{id}/source                 -> StackResponse; Editor on the stack; 409 while a deployment is unfinished
//   POST /api/stacks/{id}/restart                -> StackCommandResponse; Operator; ?timeoutSeconds=0..300, default 10
//   POST /api/stacks/{id}/down                   -> StackCommandResponse; Editor; ?timeoutSeconds=0..300, default 10
//   GET  /api/stacks/{id}/deployments            -> DeploymentResponse[] (newest first, at most 50)
//   POST /api/stacks/{id}/deployments            -> 202 DeploymentDetailsResponse; Editor; queued, see the deployment for the outcome
//   GET  /api/deployments/{id}                   -> DeploymentDetailsResponse with the log
//
// A stack or deployment the user may not see is 404, the same as a missing one. Restart and down answer 200
// also when compose itself failed (the output says why); errors before compose ran are problem responses:
// 409 for a system stack or a deployment in progress, 404 for a stack that was never deployed on the host,
// 503 when the agent is offline (nothing is queued), 502 for an agent error.
//
// An access token is write-only: responses only say whether one is set.

/// <param name="AccessUserName">User name sent with the token; null for the default or without a token.</param>
public sealed record RepositoryResponse(
    Guid Id, string Name, string Url, bool HasAccessToken, string? AccessUserName, DateTimeOffset CreatedAt);

/// <param name="AccessToken">Null for a public repository.</param>
public sealed record CreateRepositoryRequest(string Name, string Url, RepositoryAccessTokenRequest? AccessToken);

public sealed record UpdateRepositoryRequest(string Name, string Url);

/// <param name="UserName">Null for "x-access-token", which GitHub and GitLab accept with any token; GitLab deploy tokens
/// and Bitbucket need their own.</param>
public sealed record RepositoryAccessTokenRequest(string? UserName, string Token)
{
    public override string ToString() => $"{nameof(RepositoryAccessTokenRequest)} {{ UserName = {UserName} }}";
}

/// <param name="ComposePath">Path of the compose file in the repository; its directory holds the stack's files.</param>
public sealed record CreateStackRequest(string Name, Guid RepositoryId, string Branch, string ComposePath);

/// <param name="ComposePath">Path of the compose file in the repository; its directory holds the stack's files.</param>
public sealed record ChangeStackSourceRequest(Guid RepositoryId, string Branch, string ComposePath);

/// <param name="RepositoryName">Null when the repository is gone.</param>
/// <param name="DeployedCommit">The commit of the last successful deployment; null before it and after down.</param>
/// <param name="CanDeploy">Whether the user may deploy, change the source and take the stack down; for hiding buttons only.</param>
/// <param name="CanRestart">Whether the user may restart the stack; for hiding buttons only.</param>
public sealed record StackResponse(
    Guid Id,
    Guid EnvironmentId,
    string Name,
    Guid RepositoryId,
    string? RepositoryName,
    string Branch,
    string ComposePath,
    bool IsSystem,
    string? DeployedCommit,
    DateTimeOffset? DeployedAt,
    bool CanDeploy,
    bool CanRestart);

/// <param name="Error">Why compose failed, or null when it succeeded.</param>
/// <param name="Output">The last lines of compose output.</param>
public sealed record StackCommandResponse(bool Succeeded, string? Error, IReadOnlyList<string> Output);

/// <param name="Pull">True for "redeploy with pull": pull every image, not only missing ones.</param>
public sealed record DeployRequest(bool Pull);

public sealed record DeploymentResponse(
    Guid Id,
    DeploymentStatus Status,
    DeploymentTrigger Trigger,
    ImagePullPolicy PullPolicy,
    string? Commit,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

public sealed record DeploymentDetailsResponse(
    Guid Id,
    Guid StackId,
    DeploymentStatus Status,
    DeploymentTrigger Trigger,
    ImagePullPolicy PullPolicy,
    string? Commit,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    IReadOnlyList<DeploymentLogLineResponse> Log);

public sealed record DeploymentLogLineResponse(DateTimeOffset At, DeploymentLogLevel Level, string Text);
