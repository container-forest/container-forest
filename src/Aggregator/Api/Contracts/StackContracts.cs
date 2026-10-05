using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Api.Contracts;

// Repositories, stacks and deployments for the panel, limited to what the signed-in user may see and do.
//
//   GET  /api/repositories                       -> RepositoryResponse[] (name order); administrators all, editors those
//                                                  their environments may deploy from (ADR-0006); 403 for anyone else
//   POST /api/repositories                       -> 201 RepositoryResponse; administrators, or editors with a connection
//                                                  allowed in one of their environments (or none, for a public repository)
//   PUT  /api/repositories/{id}                  -> RepositoryResponse; administrators, or editors who may deploy every
//                                                  stack of the repository; 400 for a URL outside the connection's prefix
//   POST /api/repositories/{id}/webhook          -> WebhookSecretResponse; as PUT; a new secret, the old one stops working
//   DELETE /api/repositories/{id}/webhook        -> RepositoryResponse; as PUT; pushes are no longer received
//   GET  /api/environments/{id}/stacks           -> StackResponse[] (name order)
//   POST /api/environments/{id}/stacks           -> 201 StackResponse; Editor on the environment
//   GET  /api/stacks/{id}                        -> StackResponse
//   PUT  /api/stacks/{id}/source                 -> StackResponse; Editor on the stack; 409 while a deployment is unfinished
//   PUT  /api/stacks/{id}/deploy-on-push         -> StackResponse; Editor on the stack; 409 for a system stack
//   PUT  /api/stacks/{id}/restart-on-file-change -> StackResponse; Editor on the stack; 409 for a system stack
//   POST /api/stacks/{id}/deploy-hook            -> DeployHookTokenResponse; Editor on the stack; 409 for a system stack;
//                                                  a new token, the old one stops working
//   DELETE /api/stacks/{id}/deploy-hook          -> StackResponse; Editor on the stack; calls are no longer accepted
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
// Access tokens belong to git connections (GitConnectionContracts); a repository without a connection is public. A
// repository or connection the user may not use is 404. The webhook secret is shown once, in the response that
// creates it. The git server sends deliveries to POST /api/webhooks/{repositoryId} (WebhookContracts). The deploy
// hook token is shown once too, and CI calls POST /api/deploy-hooks/{stackId} with it (WebhookContracts).

/// <param name="ConnectionId">The git connection the repository is fetched through; null for a public repository.</param>
/// <param name="ConnectionName">Name of that connection.</param>
/// <param name="HasWebhook">A webhook secret is set: pushes deploy the stacks that opted in.</param>
/// <param name="ConnectionHasWebhook">The repository's connection has an organization-wide webhook, which also delivers its pushes.</param>
/// <param name="CanManage">Whether the current user may change the repository and its webhook.</param>
public sealed record RepositoryResponse(
    Guid Id,
    string Name,
    string Url,
    Guid? ConnectionId,
    string? ConnectionName,
    bool HasWebhook,
    bool ConnectionHasWebhook,
    bool CanManage,
    DateTimeOffset CreatedAt);

/// <param name="Secret">Shown only in this response; the git server signs deliveries with it.</param>
public sealed record WebhookSecretResponse(RepositoryResponse Repository, string Secret)
{
    public override string ToString() => $"{nameof(WebhookSecretResponse)} {{ Repository = {Repository} }}";
}

public sealed record DeployOnPushRequest(bool Enabled);

public sealed record RestartOnFileChangeRequest(bool Enabled);

/// <param name="Token">The deploy hook's token, shown only here: the aggregator keeps its hash.</param>
public sealed record DeployHookTokenResponse(StackResponse Stack, string Token)
{
    public override string ToString() => $"{nameof(DeployHookTokenResponse)} {{ Stack = {Stack} }}";
}

/// <param name="Url">Under the connection's URL prefix, when there is a connection.</param>
/// <param name="ConnectionId">Null for a public repository.</param>
public sealed record CreateRepositoryRequest(string Name, string Url, Guid? ConnectionId);

/// <param name="Url">Under the connection's URL prefix, when there is a connection.</param>
/// <param name="ConnectionId">Null for a public repository.</param>
public sealed record UpdateRepositoryRequest(string Name, string Url, Guid? ConnectionId);

/// <param name="ComposePath">Path of the compose file in the repository; its directory holds the stack's files.</param>
public sealed record CreateStackRequest(string Name, Guid RepositoryId, string Branch, string ComposePath);

/// <param name="ComposePath">Path of the compose file in the repository; its directory holds the stack's files.</param>
public sealed record ChangeStackSourceRequest(Guid RepositoryId, string Branch, string ComposePath);

/// <param name="RepositoryName">Null when the repository is gone.</param>
/// <param name="DeployedCommit">The commit of the last successful deployment; null before it and after down.</param>
/// <param name="DeployOnPush">A push to the branch deploys the stack, when the repository has a webhook.</param>
/// <param name="RestartOnFileChange">A deployment restarts the stack when its files other than the compose file changed.</param>
/// <param name="HasDeployHook">CI can deploy the stack with fresh images through its deploy hook.</param>
/// <param name="CanDeploy">Whether the user may deploy, change the source and take the stack down; for hiding buttons only.</param>
/// <param name="CanRestart">Whether the user may restart the stack; for hiding buttons only.</param>
/// <param name="CanRemove">Whether the user may remove the stack (an Editor of its environment); for hiding buttons only.</param>
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
    bool DeployOnPush,
    bool RestartOnFileChange,
    bool HasDeployHook,
    bool CanDeploy,
    bool CanRestart,
    bool CanRemove);

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
