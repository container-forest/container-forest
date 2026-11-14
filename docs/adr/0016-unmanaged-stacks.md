# ADR-0016. Unmanaged stacks: compose projects a host's administrator starts by hand

- Status: accepted
- Date: 2026-11-13

## Context

A stack in the system is a `Stack` record deployed from git (ADR-0004). But every host has compose projects outside git: a reverse proxy, a database, a CI runner, game servers. They live in, say, `/srv/docker/<name>`, and the host's administrator starts them by hand. Their containers already show in the panel as containers outside managed stacks: they can be started, stopped and restarted, and their logs read (the permission is a role on the whole environment). But nothing can be done with the project itself: `compose pull`, `up`, `restart` and `down` still need SSH to the host. Removing a stopped container isn't possible for any container either.

Turning them into managed stacks isn't always worth it. Some are system services (the panel's Caddy, the agent itself), some hold state and configuration that isn't in git, and some the host's administrator simply wants to keep changing by hand.

Options considered:

1. **The aggregator sends the path to the compose file.** The simplest, but then the aggregator (or someone with a compromised aggregator) makes the agent, as root, read a compose file anywhere on the host and run it. Rejected.
2. **The agent finds projects only by container labels** (`com.docker.compose.project.working_dir`, `config_files`). No configuration needed, but after `down` there are no containers, the project disappears from the list, and it can't be brought up again from the panel. The agent would also run compose in any directory it had ever been run from.
3. **Roots in the agent's settings** (chosen). The host's administrator explicitly opens directories (`Agent:UnmanagedStackRoots`, e.g. `/srv/docker`) and mounts them into the agent at the same path. The agent finds projects both by container labels and by compose files in these directories.

## Decision

1. **What an unmanaged stack is.** A compose project all of whose files (the project directory, the compose files, the `--env-file`) lie in one of `Agent:UnmanagedStackRoots`. It's found in two ways:
   - by container labels: the project's newest container is used;
   - by scanning the roots to depth 3. Scanning doesn't descend into subdirectories of a directory with a compose file, into hidden directories, or through symbolic links. The project name is determined as compose does: `COMPOSE_PROJECT_NAME` from `.env`, then `name:` in the file, then the normalised directory name. If the name depends on interpolation, the project is skipped.

   Projects in the agent's stacks directory (`StacksDirectory`) are never unmanaged, and the roots can't overlap the agent's own directories (checked at start). Without roots the feature is off, and the agent answers with an empty list.

2. **The aggregator names a stack only by name.** `ControlUnmanagedStack` carries the project's name and the operation. The agent finds the stack itself every time and runs compose with the directory, files and env files the administrator ran it with. That way the aggregator can't make the agent read a file outside the roots (invariant 5 holds: there are four operations, all fixed).

3. **Operations:** `up -d`, `pull`, `restart`, `down`. Restart and down, as for managed stacks, run without `-f`, only with `-p`, by container labels, so a compose file that's gone doesn't get in their way. Up runs without `--remove-orphans` and without `--wait`: the agent doesn't know what's in the stack and mustn't clean up something that isn't its own.

4. **Permissions are only a role on the whole environment**, since there's no `Stack` record to grant a role on. Restart needs Operator; pull, up and down need Editor, as for managed stacks. A user without a role on the environment doesn't see unmanaged stacks, just as they don't see containers outside stacks. A managed stack's name never becomes unmanaged, even if the user can't see that managed stack.

5. **System stacks.** If any container of the project has the `com.container-forest.system` label, the aggregator refuses (409) every operation: up or down of the agent or the panel's Caddy would cut the connection the command travels on.

6. **Registry credentials.** The aggregator doesn't read the stack's compose file, so it doesn't know which registries are needed. For pull and up the agent gets all the environment's credentials, as for a managed stack with an interpolated image.

7. **Removing a container** (`RemoveContainer`) is `docker rm` without `-f` and without anonymous volumes, only for a container in the created, exited or dead state. It's available for any non-system container, with the Deploy permission (Editor): only compose up or a deployment brings the container back with its configuration.

8. **Compatibility.** All of this is new `oneof` cases. An old agent answers `ERROR_CODE_UNSUPPORTED`. The aggregator then shows unmanaged stacks as empty (the bot) or as an agent error (the panel and the CLI). The bot gets unmanaged stacks in `ListStacks` with `unmanaged = true` and controls them through `ControlStack` with the new `STACK_ACTION_UP` and `STACK_ACTION_PULL`.

## Consequences

- **Whoever can change the compose file controls what an environment's Editor will start.** An unmanaged stack's compose file doesn't go through the compose policy (ADR-0012): an environment's Editor can bring up a privileged stack that the host's administrator put there. They can't change the file through the system, so with a trusted host administrator no privileges are added. But a container with read-write access to its directory in the root can rewrite its own compose file, and the next up by an Editor will run it. So open a root deliberately; the agent's documentation says a read-only mount is enough.
- **Host paths are visible in the panel** to everyone with a role on the environment: the project directory and the compose files. They hold no secrets. The aggregator doesn't receive the files' content.
- **No credentials from the host.** The agent works with its own `DOCKER_CONFIG` and doesn't see the host administrator's `docker login`. An unmanaged stack's private image is pulled only with registry credentials set for the environment in the aggregator.
- **`StacksDirectory` may not exist yet:** restart and down of an unmanaged stack run in it, as for managed stacks, so the agent creates it before running compose. On a host without managed stacks this directory wouldn't otherwise exist.
