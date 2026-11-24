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
//   PUT  /api/stacks/{id}/require-signed-commits -> StackResponse; Editor on the stack; 409 for a system stack (ADR-0011)
//   PUT  /api/stacks/{id}/require-image-digests -> StackResponse; Editor on the stack; 409 for a system stack
//   PUT  /api/stacks/{id}/policy-allowances      -> StackResponse; administrators of the system or of the environment;
//                                                  the stack's exceptions to the compose policy (ADR-0012, PolicyContracts)
//   PUT  /api/stacks/{id}/policy-unrestricted    -> StackResponse; as policy-allowances; the policy does not limit the stack
//   POST /api/stacks/{id}/deploy-hook            -> DeployHookTokenResponse; Editor on the stack; 409 for a system stack;
//                                                  a new token, the old one stops working
//   DELETE /api/stacks/{id}/deploy-hook          -> StackResponse; Editor on the stack; calls are no longer accepted
//   POST /api/stacks/{id}/image-hook             -> ImageHookTokenResponse; Editor on the stack; 409 for a system stack;
//                                                  a new token for the image hook (ADR-0015), the old one stops working
//   PUT  /api/stacks/{id}/image-hook             -> StackResponse; Editor on the stack; whether the hook may roll back
//   DELETE /api/stacks/{id}/image-hook           -> StackResponse; Editor on the stack; calls are no longer accepted
//   POST /api/stacks/{id}/restart                -> StackCommandResponse; Operator; ?timeoutSeconds=0..300, default 10
//   POST /api/stacks/{id}/down                   -> StackCommandResponse; Editor; ?timeoutSeconds=0..300, default 10
//   GET  /api/stacks/{id}/deployments            -> DeploymentResponse[] (newest first, at most 50)
//   POST /api/stacks/{id}/deployments            -> 202 DeploymentDetailsResponse; Editor; queued, see the deployment for the outcome;
//                                                  approveCommit deploys that unsigned commit to a stack requiring signed ones
//   POST /api/stacks/{id}/rollback               -> 202 DeploymentDetailsResponse; Editor; deploys a commit the stack deployed
//                                                  successfully before (400 for any other), then holds the stack at it
//   DELETE /api/stacks/{id}/hold                 -> StackResponse; Editor; pushes, the deploy hook and image updates deploy
//                                                  the stack again; nothing is deployed now
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
/// <param name="CanManage">Whether the current user may change or remove the repository and its webhook.</param>
/// <param name="StackCount">How many stacks deploy from it; only when the user may manage it. One with stacks cannot be removed.</param>
public sealed record RepositoryResponse(
    Guid Id,
    string Name,
    string Url,
    Guid? ConnectionId,
    string? ConnectionName,
    bool HasWebhook,
    bool ConnectionHasWebhook,
    bool CanManage,
    int? StackCount,
    DateTimeOffset CreatedAt);

/// <param name="Secret">Shown only in this response; the git server signs deliveries with it.</param>
public sealed record WebhookSecretResponse(RepositoryResponse Repository, string Secret)
{
    public override string ToString() => $"{nameof(WebhookSecretResponse)} {{ Repository = {Repository} }}";
}

public sealed record DeployOnPushRequest(bool Enabled);

public sealed record RestartOnFileChangeRequest(bool Enabled);

public sealed record RequireSignedCommitsRequest(bool Enabled);

public sealed record RequireImageDigestsRequest(bool Enabled);

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
/// <param name="RequireSignedCommits">Only commits signed by someone who may deploy the stack, or approved by SHA, are deployed (ADR-0011).</param>
/// <param name="RequireImageDigests">Every image must be pinned as name@sha256:….</param>
/// <param name="CanDeploy">Whether the user may deploy, change the source and take the stack down; for hiding buttons only.</param>
/// <param name="CanRestart">Whether the user may restart the stack; for hiding buttons only.</param>
/// <param name="CanRemove">Whether the user may remove the stack (an Editor of its environment); for hiding buttons only.</param>
/// <param name="PolicyAllowances">The stack's own exceptions to the environment's compose policy, e.g. "docker-socket" (ADR-0012).</param>
/// <param name="CanAdministerEnvironment">Whether the user may change the stack's allowances; for hiding buttons only.</param>
/// <param name="PolicyUnrestricted">The compose policy does not limit the stack: its Editors are root on the host (ADR-0012).</param>
/// <param name="HasImageHook">CI can ask for the stack's image policies to be checked now (ADR-0015).</param>
/// <param name="ImageHookAllowsDowngrade">The image hook may ask for an older tag, which pins the image there.</param>
/// <param name="HeldCommit">
/// The commit a rollback deployed: pushes, the deploy hook and image updates do not deploy the stack until someone deploys
/// the branch by hand or releases the hold. Null when the stack is not held.
/// </param>
/// <param name="HeldSince">When that rollback finished.</param>
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
    bool RequireSignedCommits,
    bool RequireImageDigests,
    bool CanDeploy,
    bool CanRestart,
    bool CanRemove,
    IReadOnlyList<string> PolicyAllowances,
    bool CanAdministerEnvironment,
    bool PolicyUnrestricted,
    bool HasImageHook = false,
    bool ImageHookAllowsDowngrade = false,
    string? HeldCommit = null,
    DateTimeOffset? HeldSince = null);

/// <param name="AllowDowngrade">The hook may ask for a tag older than the deployed one; the image is then pinned there.</param>
public sealed record ImageHookRequest(bool AllowDowngrade);

/// <param name="Token">The image hook's token, shown only here: the aggregator keeps its hash.</param>
public sealed record ImageHookTokenResponse(StackResponse Stack, string Token)
{
    public override string ToString() => $"{nameof(ImageHookTokenResponse)} {{ Stack = {Stack} }}";
}

public sealed record PolicyUnrestrictedRequest(bool Enabled);

/// <param name="Allowances">The whole list; it replaces the previous one.</param>
public sealed record SetPolicyAllowancesRequest(IReadOnlyList<string> Allowances);

/// <param name="Error">Why compose failed, or null when it succeeded.</param>
/// <param name="Output">The last lines of compose output.</param>
public sealed record StackCommandResponse(bool Succeeded, string? Error, IReadOnlyList<string> Output);

/// <param name="Pull">True for "redeploy with pull": pull every image, not only missing ones.</param>
/// <param name="ApproveCommit">
/// For a stack that requires signed commits: the full SHA of an unsigned commit to deploy anyway. It is deployed only
/// while it is still the branch head; omitted to deploy only an endorsed commit.
/// </param>
public sealed record DeployRequest(bool Pull, string? ApproveCommit = null);

/// <param name="Commit">The full SHA of a commit the stack deployed successfully before.</param>
/// <param name="Approve">For a stack that requires signed commits: deploy the commit even if nobody's signature endorses it.</param>
public sealed record RollbackRequest(string Commit, bool Approve = false);

/// <summary>Who endorsed the deployed content (ADR-0011).</summary>
/// <param name="Status">NotChecked when the stack does not require signed commits.</param>
/// <param name="Problem">Why the commit was not endorsed; with Rejected, and with ApprovedUnsigned for what the approval overrode.</param>
/// <param name="SignedBy">The name of the user whose key signed the commit, as it was then.</param>
/// <param name="KeyFingerprint">"SHA256:…" of that key.</param>
/// <param name="ApprovedCommit">The unsigned commit the user who started the deployment approved.</param>
/// <param name="ImageUpdateId">The aggregator's image update that endorsed the commit or queued the deployment (ADR-0015).</param>
public sealed record EndorsementResponse(
    CommitEndorsement Status,
    EndorsementProblem? Problem,
    string? SignedBy,
    string? KeyFingerprint,
    string? ApprovedCommit,
    Guid? ImageUpdateId = null);

public sealed record DeploymentResponse(
    Guid Id,
    DeploymentStatus Status,
    DeploymentTrigger Trigger,
    ImagePullPolicy PullPolicy,
    string? Commit,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    CommitEndorsement Endorsement,
    string? SignedBy);

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
    IReadOnlyList<DeploymentLogLineResponse> Log,
    EndorsementResponse Endorsement);

public sealed record DeploymentLogLineResponse(DateTimeOffset At, DeploymentLogLevel Level, string Text);
