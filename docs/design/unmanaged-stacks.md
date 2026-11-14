# Unmanaged stacks

Compose projects that a host's administrator starts by hand. The decision and reasoning are in [ADR-0016](../adr/0016-unmanaged-stacks.md). Aggregator paths are relative to `src/Aggregator`, agent paths to `src/Agent`, panel paths to `src/Web`.

## Host setup

Add the root to the agent's `deploy/agent/compose.yaml` and mount it at the same path (read-only is enough):

```yaml
    environment:
      CF_AGENT__UNMANAGEDSTACKROOTS__0: /srv/docker
    volumes:
      - /srv/docker:/srv/docker:ro
```

The agent skips a root that doesn't exist in its container, with a warning in the log. A root that overlaps `DataDirectory`, `StacksDirectory` or `SecretsDirectory` is a configuration error at start.

## Agent

- **Discovery:** `UnmanagedStackFinder`. Stacks by label come from `IDockerGateway.ListComposeContainersAsync` (filter `label=com.docker.compose.project`, labels `working_dir`, `config_files`, `environment_file`, see `ContainerInfoMapper.ToComposeContainer`); stacks from directories come from `Scan`. When names match, the stack with containers wins, and scanning skips a directory that already belongs to a project with containers. Limits: depth `MaxScanDepth` (3), at most `MaxScannedDirectories` (2000) directories per root, `.env` and the compose file for the name up to 1 MB.
- **Name:** `UnmanagedStackFinder.ProjectNameOf`. So that a name found by scanning matches what compose would choose, the agent always passes `-p`.
- **Commands:** `ListUnmanagedStacksHandler` and `ControlUnmanagedStackHandler` (`Commands/UnmanagedStackHandlers.cs`), locked by the same `StackLocks` as managed stacks. Arguments come from `ComposeInvocation.UnmanagedUp` and `UnmanagedPull` (`-p`, `--project-directory`, `-f` for each file, `--env-file`, the project directory as the working directory). Restart and down use the same `ComposeInvocation.Restart` and `Down` as managed stacks: no `-f`, in `StacksDirectory`.
- **Removing a container:** `RemoveContainerHandler` → `DockerDotNetGateway.RemoveContainerAsync` (`Force = false`, `RemoveVolumes = false`). Docker doesn't remove a running container, and the agent returns its error as `ERROR_CODE_COMMAND_FAILED`.

## Aggregator

- **Service:** `Application/Stacks/UnmanagedStackService`. For a user without a role on the whole environment, `ListAsync` returns an empty list without asking the agent. `ExecuteAsync` checks, in this order:
  1. the environment is visible, else 404;
  2. there's a role on the environment and the name is valid, else 404;
  3. the name doesn't belong to a managed stack and the agent found such a stack, else 404;
  4. the stack isn't a system stack, else 409;
  5. the role is sufficient: Operator for restart, Editor for the rest, else 403;
  6. registry credentials for pull and up;
  7. the agent.

  The stop timeout and output (the last 1000 lines) work as in `StackCommand`. The request to the agent goes without the caller's token.
- **Audit:** `UnmanagedStackStarted`, `UnmanagedStackPulled`, `UnmanagedStackRestarted`, `UnmanagedStackTakenDown` (category Stacks, target `StackAuditTarget` without an id), `ContainerRemoved` (Containers).
- **REST:** `Api/Endpoints/EnvironmentEndpoints.cs`; the list is in a comment in `Api/Contracts/EnvironmentContracts.cs`. `ContainerResponse.canRemove` is the Deploy permission, always false for a system container.
- **Removing a container:** `RemoveContainerCommand` in `ContainerCommandService`. Through `AuthorizedCommand` it needs `Permission.Deploy` (`IContainerCommand.RequiredPermission` can now be overridden), and through `IsAllowedIn` the container must be created, exited or dead.

## Bot

`ListStacks` adds unmanaged stacks to managed ones (`unmanaged`, `can_up`, `can_pull`; `id` is empty). The aggregator swallows the agent's error, including an old agent's `Unsupported`, so managed stacks stay visible when the agent is offline. `ControlStack` first looks for a visible managed stack and otherwise goes to `UnmanagedStackService`. Up and pull of a managed stack are `FAILED_PRECONDITION`. Commands: `/stack-up`, `/stack-pull` (unmanaged only), `/stack-restart`, `/stack-down` (both kinds), `/remove` (a container). Autocomplete offers unmanaged stacks along with deployed ones.

## CLI

`forest unmanaged list|up|pull|restart|down <stack> [-e <environment>]`; down requires `--yes`, restart and down take `-t`. `forest container remove <container> --yes`.

## Panel

An *Unmanaged stacks* section under the stacks table on `/stacks` (`components/stacks/unmanaged-stacks-section.tsx`, rules in `features/unmanaged-stacks/model.ts`). The section isn't shown when the agent is offline, when there are no unmanaged stacks, or when the user has no role on the environment. A row opens the stack's page; the buttons in a row are quick actions. The screen's filters (type, state, search) apply to this section too. If the type is *Unmanaged* and there are no stacks, an explanation shows instead of an empty space. Restart and down ask for confirmation, and compose's output expands under the result message. Restart and down are disabled for a stack without containers. The `ContainersChanged` event also invalidates the list of unmanaged stacks. The remove button in `ContainerTable` appears only with `canRemove` and works only on a stopped container; its confirmation has no stop timeout.

The stack page is `/stacks/unmanaged/$environmentId/$stackName` (`routes/_authed/stacks/unmanaged.$environmentId.$stackName.tsx`). The aggregator has no separate endpoint: the page takes the stack from `unmanagedStacksQuery` by name, so a stack the agent no longer finds shows as "not found". Cards:

- **Actions:** up, pull, restart and down with the same rules as in the section; for a system stack, an explanation instead of buttons.
- **Containers:** `StackContainersCard` (`components/stacks/stack-containers-card.tsx`), shared with managed stacks: the environment's containers with this `stackName`.
- **Files:** the project directory, the compose files and how the stack was found (by labels or by scanning a directory).
- **Recent actions:** administrators only, since the audit log is theirs. The query is `auditQuery` by environment, category Stacks and a search by name. The log's search matches substrings, so `isUnmanagedStackEntry` keeps entries with exactly this name, no `stackId` and `UnmanagedStack*` actions. Shows the last five and a link to the log with the same filters. An action's mutation also invalidates `["audit"]`. In the audit log, such an entry links to the stack's page (`targetLink`).
