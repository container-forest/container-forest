# Webhooks, deploy hooks and image hooks

How to use them: [architecture.md](../architecture.md#deploy-on-push-webhook); the organisation webhook: [ADR-0006](../adr/0006-git-connections.md). Paths are relative to `src/Aggregator`.

## Webhook deployments

- **Secrets:** on a repository (`GitRepository.ProtectedWebhookSecret`, `ISecretProtector` with `SecretPurpose.WebhookSecret`, `POST|DELETE /api/repositories/{id}/webhook`) and on a connection (`GitConnection.ProtectedWebhookSecret`, `/api/git-connections/{id}/webhook`). Shown once; a new one replaces the old.
- **Turning it on:** `Stack.DeployOnPush` (`PUT /api/stacks/{id}/deploy-on-push`; 409 for a system stack). One webhook per repository serves all its stacks, and whoever is responsible for a stack decides.
- **Receiving:** `PushWebhookService`, and `ReceiveForConnectionAsync` for an organisation: the repository is found by the URL in the body (`PushEvent.RepositoryUrls`) through `GitUrl.Key`; not found means `Ignored`.
- **Verification.** The secret itself authenticates the request. The body is read raw (up to 5 MiB, else 413), since the signature covers the exact bytes.
  - Providers form a hierarchy `GitWebhookProvider` → `HmacWebhookProvider` → `GitHubWebhook`, `GiteaWebhook` (also Forgejo); `GitLabWebhook` compares `X-Gitlab-Token` with the secret. Gitea also sends GitHub's headers, so its provider is asked first.
  - Every comparison is `CryptographicOperations.FixedTimeEquals` over the SHA-256 of both values (which doesn't reveal the length either).
  - An unknown repository, a repository without a secret, an unknown sender, a wrong signature and an unreadable secret all get the same 401 (`WebhookRejectedException`); a body that isn't JSON gets 400 only after the signature check. Neither the body nor the signature is logged.
- **Deployment:** `Deployment.QueueFromWebhook` (trigger `Webhook`, no user, `pull: missing`) for stacks on the same branch with `DeployOnPush`. The deployment takes the branch head, not `after` from the body: replaying an old delivery redeploys the current head rather than rolling the stack back.
- **The response** (`WebhookResponse`) is for the git server's delivery log:

  | Result | When |
  |---|---|
  | `Pong` | a GitHub `ping` |
  | `Ignored` | other events, tags, a deleted branch, repository not found |
  | `NotAllowed` | the connection is no longer allowed in the stack's environment |
  | `Held` | the stack is held after a rollback (ADR-0018) and isn't deployed |
  | `AlreadyDeployed` | the stack already runs commit `after` (a repeated delivery) |
  | `AgentOffline` | the agent is offline and nothing is queued (invariant 7) |
  | `AlreadyQueued` | a deployment is queued: its status stays `Running` until the fetch, so it will still pick up this push |
  | `AlreadyRunning` | the running deployment has the same commit |
  | `AfterRunning` | a deployment with another commit is running: after it, `PushFollowUpObserver` queues a new one if the agent is online and nobody queued their own |

- **Deploying after the current one:** how `PushDeploymentGate` avoids losing a push is described in its comment. After an aggregator restart, the push has to be redelivered from the git server.

## Deploy hook

- **Token:** on the stack, only its hash is stored (`Stack.DeployHookTokenHash`, `SecretToken.Hash`, constant-time check in `AcceptsDeployHook`). A stack's Editor creates and deletes it (`POST|DELETE /api/stacks/{id}/deploy-hook`, `StackService`); 409 for a system stack.
- **Call:** `DeployHookService`; the body is ignored: `Deployment.QueueFromDeployHook` (trigger `DeployHook`, no user, the branch head), 202 with `Queued`. The token goes in a header because proxy and CI logs keep URLs.
- **During another deployment:** a queued one that pulls all images anyway means `AlreadyQueued`; any other unfinished one means `AfterRunning`. `PushDeploymentGate` remembers the pull policy: a later push doesn't override the hook's `Always`, and `PushFollowUpObserver` queues `QueueFromDeployHook` and, if someone queued a deployment without pull in between, waits for that one too.
- **Refusals:** an unknown stack, a stack without a hook, and a missing or wrong token all get the same 401 (`WebhookRejectedException`); an offline agent gets 503 and nothing is queued; a stack held after a rollback (ADR-0018) gets 409.

## Image hook

A stack's image hook (ADR-0015) asks the aggregator to check the image policies now, or to update an image to a specific tag; the aggregator makes the commit and the deployment itself. The token is `Stack.ImageHookTokenHash`, like the deploy hook's but separate: the deploy hook redeploys the branch head, the image hook changes a tag within the policy. Details in [image-updates.md](image-updates.md).
