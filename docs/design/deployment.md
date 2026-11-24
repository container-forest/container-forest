# Stack deployment

Aggregator paths are relative to `src/Aggregator`.

## Pipeline and queue

- **Steps:** `IDeployStep`s are registered in `AddApplication` in execution order: `GitFetchStep` (for a pinned deployment, its commit rather than the branch head) → `VerifyCommitStep` → `SopsDecryptStep` → `ValidateComposeStep` → `BuildBundleStep` → `ResolveRegistryAuthStep` → `ComposePolicyStep` → `SendToAgentStep`. A new stage is a new class and a registration line; `DeployPipeline` doesn't change.
- **Between steps:** results are passed through `DeployContext`. `DeploymentFailedException` is an error the user sees; other exceptions hide behind "Internal error" and go to the aggregator's log.
- **Queue:** a deployment is only queued (`IDeploymentQueue`, 202 Accepted); `DeploymentWorker` runs it in its own DI scope, up to `Deployments:MaxConcurrentDeployments` in parallel.
- **Pinned deployments** (`Deployment.IsPinned`): rehydrate, image updates (`QueueFromImageUpdate`, trigger `ImageUpdate`, ADR-0015) and rollbacks (`QueueRollback`, trigger `Rollback`, ADR-0018) deploy their own commit, not the branch head. Rehydrate and image updates, like the webhook and the deploy hook, are automatic (`IsAutomatic`); a rollback, like a manual deployment, is by hand (`IsByHand`).
- **Limits:** one unfinished deployment per stack (409). Nothing is queued for an offline agent (503). After an aggregator restart, unfinished deployments are marked Failed rather than resumed.
- **Log:** `DeploymentLogLine` rows in the `DeploymentLogLines` table, up to 5000 lines. Compose output from the agent is collected during the step and copied into the log after it: `Deployment` isn't thread-safe, and the progress callback runs on the gRPC read loop.

## Rollback and hold

ADR-0018. `DeploymentService.RollbackAsync`, `POST /api/stacks/{id}/rollback` (`RollbackRequest`: the full SHA, `approve`), `forest stack rollback <stack> --to <sha or prefix> [--approve]`, *Roll back to this commit* on a successful deployment's screen.

- **Which commit:** only one a deployment of this stack deployed successfully (`IDeploymentRepository.HasSucceededAsync`), otherwise 400. The role is a redeploy's (Editor); an offline agent, a system stack and an unfinished deployment are refused as for a manual deployment.
- **Checks:** the whole pipeline. `VerifyCommitStep` evaluates the endorsement again; the commit not descending from the deployed one is only an info line. With `approve` the deployment's `ApprovedCommit` is the commit itself (`RecordApprovedUnsigned` accepts it for a rollback), and a rejected rollback's screen offers approving it as a rollback again.
- **Hold:** `Stack.RecordDeployment` of a successful rollback sets `HeldCommit` and `HeldSince`; a successful manual deployment clears them, rehydration keeps them. While held, `PushWebhookService` answers `Held` for the stack, and `PushFollowUpObserver` drops a push or hook call that arrived during the rollback; `DeployHookService` throws a conflict (409); `ImageUpdateService.CheckAsync` returns `Held` for the poller, the hook and *Check now*, and a conflict for pinning a tag, except for a dry run.
- **Releasing:** `StackService.ReleaseHoldAsync`, `DELETE /api/stacks/{id}/hold` (Editor, audit `StackHoldReleased`), `forest stack release-hold`, the hold card on the stack's screen. Nothing is deployed.
- **What doesn't change:** the agent gets an ordinary `DeployStack`; `.prev` isn't used.

## Registry credentials

- The aggregator sends `RegistryAuth` only for the hosts of the stack's images. For an image with `${VAR}` or with `extends`, all of the environment's credentials.
- The Docker Hub key in config.json is `https://index.docker.io/v1/` (`RegistryHost.ConfigKey`).
- The agent writes `config.json` to `<SecretsDirectory>/.docker-config/<guid>` (0700/0600), passes `DOCKER_CONFIG` to the compose process and deletes the directory afterwards. With its own `DOCKER_CONFIG` the CLI doesn't see the user's contexts, so it uses the default socket.

## Restart after file changes

`Stack.RestartOnFileChange`, off by default; `PUT /api/stacks/{id}/restart-on-file-change`, Editor, 409 for a system stack; `DeployStack.restart_on_file_change`.

**Why:** containers read mounted files (configs, file secrets) only at start, and `compose up` recreates a container only when its definition changed. A new config without a restart sits on the host but has no effect.

- **Detection:** before writing, `StackFileStore.WriteAsync` compares the bundle's files, except `compose.yaml`, with those on the host (secrets with the tmpfs copies) and with the manifest. A link where a regular file should be counts as a change and isn't read, a missing file is also a change, and a stack's first deployment isn't.
- **Restart:** if the option is on and files changed, `DeployStackHandler` runs `compose restart` for the whole stack after `compose up` (a 10 s timeout, as in the panel), without `-f`, like `RestartStackHandler`. The output goes to the deployment log.
- **Simplification:** containers that `up` has just recreated are restarted too: simpler than mapping files to services.
- **Compatibility:** an agent without this field just doesn't restart.

## Stack restart and down

`Application/Stacks/StackCommand.cs`, `StackCommandService`, the hierarchy `StackCommand` → `RestartStackCommand`, `DownStackCommand`. Endpoints: `POST /api/stacks/<id>/restart|down?timeoutSeconds=N`.

**Checks:**

- The permission follows `RolePermissions.RequiredFor`: Operator for restart, Editor for down.
- An invisible stack is a 404 before reaching the agent.
- A system stack is a 409 (`EnsureAllows`, `RequiresUpdateHelper`).
- An unfinished deployment of the stack is a 409, since the agent's `ERROR_CODE_BUSY` covers only the time compose is already running.

**Execution:**

- Synchronous, without a queue and without an entry in the deployment history: compose's output (the last 1000 lines) comes back in the response.
- The stop timeout is per container, 0–300 s, 10 s by default.
- The request to the agent goes without the caller's token: a closed tab mustn't abort a `down` halfway, leaving some containers stopped. The aggregator waits `AgentGateway:StackCommandTimeout` (16 minutes, a minute longer than `Agent:ComposeTimeout`).

**Responses:** an error from compose itself (the agent's `ERROR_CODE_COMMAND_FAILED`, `ERROR_CODE_TIMEOUT`) is a result with `error` and the output (HTTP 200). Other agent errors are problem details: `ERROR_CODE_NOT_FOUND` for a stack the agent never deployed is a 404, `ERROR_CODE_BUSY` a 409.

**After down:** a successful down clears `DeployedCommit` and `DeployedAt` (`Stack.RecordTakenDown`), since no containers of that commit remain, although the stack's files stay on the host. A failed down leaves them alone: some containers may still run. The bot doesn't offer such a stack in autocomplete.

**The bot** calls the same through `ListStacks` and `ControlStack` in `bot.proto`:

- The stack is given by name within the environment. An unknown or invalid name and an invisible stack all get the same `NOT_FOUND`.
- `can_restart` and `can_take_down` are always false for a system stack.
- `/stack-restart` and `/stack-down` show the end of compose's output in a code block (`ReplyFormatter.WithOutput`: the last lines within 2000 characters, with ` ``` ` in the output broken by a zero-width space).
- The call's deadline is `Bot:StackActionTimeout` (14 minutes, less than Discord's 15-minute interaction token). When the bot stops waiting, compose still runs to the end.

## The stacks API for the panel

`Api/Contracts/StackContracts.cs`. Stack and deployment visibility work as in `DeploymentService` (invisible is a 404). Everyone who sees a stack sees the repository's name in `StackResponse`.

**Creating and changing a stack**

- Creating a stack and changing its source accept only a repository allowed in the stack's environment (`GitRepository.IsUsableIn`), otherwise 404.
- An Editor on the stack changes the source (repository, branch, compose file) (`StackService.ChangeSourceAsync`), but not during an unfinished deployment (409): a queued deployment reads the source when it starts.
- A stack's name doesn't change: it's the compose project and the directory on the host.

**Deleting a stack** (`DELETE /api/stacks/{id}`, `StackService.RemoveAsync`)

- The environment's Editor deletes it, as they add it. An Editor of the stack alone can't: other people's roles on the stack would vanish with it.
- A system stack, a deployed one (`DeployedCommit`; take it down first) and one with an unfinished deployment are a 409 (`Stack.EnsureRemovable`).
- The database cascade removes the deployment history, the roles on the stack and notification rules about it; the notification history stays.
- The stack's files stay on the host, as after down: the agent has no command to remove them, and a new stack with the same name deploys over them.

**Deleting a repository** (`DELETE /api/repositories/{id}`, `RepositoryService.RemoveAsync`)

- Whoever may change it deletes it, only when no stack deploys from it. Otherwise a 409 with the count and no names, since the stacks may be in other people's environments; `RepositoryResponse.stackCount` is only for `canManage`.
- After saving, `IGitSource.ForgetAsync` deletes the clone from `git-cache` (an error only goes to the log).

**Deleting an environment** (`DELETE /api/environments/{id}`, `EnvironmentService.RemoveAsync`)

- An administrator deletes it, only without stacks (409).
- The database cascade removes the age keys (files encrypted only to them can no longer be decrypted by the aggregator), registry credentials, roles, notification rules and its place in connections.
- The agent is disconnected after saving and can't connect again.

## Pitfalls

Images tagged `latest` or without a tag break reproducibility: rolling back the stack's files doesn't bring back the previous image. `ValidateComposeStep` only **warns** in the deployment log rather than rejecting: many images are published only as `latest`, and *redeploy with pull* exists precisely for moving tags. An image with a digest (`@sha256:...`) gets no warning. `build:` in compose is rejected.
