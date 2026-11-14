# ADR-0012. Stack isolation on the host: environment administrators, the compose policy, per-stack keys

- Status: accepted
- Date: 2026-11-04

## Context

After ADR-0011 the aggregator knows **who** endorsed a stack's content, but doesn't limit **what** that content can do on the host. Any stack's compose file can:

- mount the host's `/`, `docker.sock`, or other stacks' directories and tmpfs;
- run a `privileged` container;
- join the host's `pid` or `network` namespace;
- take someone else's named volume;
- read another stack's secrets from `/run/container-forest` through `env_file`. Compose runs inside the agent, which sees that whole directory.

That's why `architecture.md` says "an environment's Editor is effectively root on the host", and "so is an Editor of a single stack". The consequences:

- The ADR-0011 signature protects only halfway: a stolen key of a person allowed to deploy one stack gives root on the host.
- ADR-0010 gave up on per-stack SOPS keys, because as long as compose isn't restricted, a separate key isolates nothing.
- Stack B's ciphertext copied into stack A's directory is decrypted by the aggregator with the environment's key and handed to A's containers.

At the same time, some people without container-forest administrator rights have SSH and root on one of the hosts. Restricting them is pointless: they'd get around any policy with `docker run`. Friction from the policy would only make them turn it off for the whole environment.

### Where to check the compose file

1. **The aggregator checks the raw YAML.** No protocol changes, before decryption. But the aggregator reads the file with YamlDotNet, and compose with go-yaml. Any discrepancy between them is a bypass: duplicate keys, YAML 1.1 booleans, tags, variable substitution from `.env`, `extends`, `include`, short and long syntax, new keys in new compose versions. Reimplementing compose means hunting for these discrepancies forever.
2. **The aggregator runs `docker compose config` itself.** Decrypted files would land on the aggregator's disk, and the process's environment would differ from the agent's.
3. **The agent runs `docker compose config` with the same binary, the same files and the same environment as `up`, and the aggregator decides** (chosen). The normalised compose model: variables substituted, `extends` and `include` merged, long syntax, absolute paths. The rules are then simple, and an unknown key means a refusal. The price is a new agent command and one more exchange during a deployment.

Checking the policy on the agent itself by sending it the rules would mean rules in two places, with versions on every agent. That would contradict invariant 4: permissions are checked only on the aggregator.

## Decision

### 1. The environment administrator role

1. A new role `Role.EnvironmentAdmin` (*Environment administrator*) above Editor. The global role stays *Administrator* (`isAdmin` in the API, `CF_BOOTSTRAP__ADMIN*` and `is_admin` in `bot.proto` don't change). In code it's always `EnvironmentAdmin`, in the panel always the full name.
2. The role is granted **only on an environment**, not on a stack. Only an administrator grants and removes it. It records a fact: the person is root on that host anyway.
3. Beyond an Editor's rights, an environment administrator manages what concerns only that environment:
   - the compose policy: the mode, the allowlist, stack allowances (point 2);
   - the Viewer, Operator and Editor roles of existing users on the environment and its stacks;
   - the age keys of the environment and its stacks, registry credentials, the agent's enrollment token, revoking the agent;
   - the environment's name and privacy.

   They can see the environment's secrets and credentials on the host anyway (tmpfs, `DOCKER_CONFIG`), so this gives no new access.
4. **What stays with the administrator:** users, creating and deleting environments, git connections, notification channels and rules, the audit log, granting the environment administrator role.
5. The permission is `Permission.AdministerEnvironment` in `AccessPolicy`. An administrator's restricted token still gives the Editor role, not environment administrator. A read-only token doesn't have this permission.

### 2. The compose policy

#### Modes

An environment has a `ComposePolicyMode`:

- `Off`: the default, behaviour as before;
- `Audit`: violations are written to the deployment log as warnings, and the deployment goes on;
- `Enforce`: a deployment with a violation fails before compose runs, and the error lists all the violations together.

The migration sets `Off` for existing environments.

#### Pipeline

`GitFetch → VerifyCommit → SopsDecrypt → ValidateCompose → BuildBundle → ResolveRegistryAuth → ComposePolicy → SendToAgent`

- **`ValidateComposeStep`** with the policy on also checks in the raw YAML that `env_file`, `label_file`, `extends.file` and `include` refer only to bundle files: relative paths without variables that are among the deployment's files. The normalised model no longer contains these references (compose inlines the content of `env_file` and `extends`), and `compose config` itself would read them from the agent's file system: from other stacks, from the secrets' tmpfs, from git or an OCI registry for a remote `include`. YAML tags (`!reset`, `!override`) are rejected under the policy.
- **`ComposePolicyStep`** renders first: the agent receives `RenderStack`, i.e. the same files and variables as for `DeployStack`. Then it:
  1. lays them out in a directory on tmpfs (`<SecretsDirectory>/.render/<guid>`, 0700, deleted right after the run);
  2. runs `docker compose -p <stack> --project-directory <directory> -f compose.yaml --env-file <the bundle's .env or /dev/null> config --format json`;
  3. translates the preparation directory's paths to the real stack directory;
  4. drops from the model what may contain secrets and isn't needed by the policy: `environment`, `command`, `entrypoint`, `healthcheck`, label values, `content` in `secrets` and `configs`, `x-*` extensions;
  5. for every bind-mount source, checks through the host root (`Agent:HostRoot`; `/:/host:ro,rslave` in `deploy/agent/compose.yaml`) whether the path has a symbolic link.

  The result (`RenderedStack`) is the model and a list of paths with links. An agent without this command answers `UNSUPPORTED`: a warning in `Audit`, a deployment error in `Enforce`.
- **Then the same step** turns the model into facts (`ComposeFacts`: only what the rules need, no secrets) and checks the rules. The facts are saved on the stack after a successful deployment (`Stack.DeployedComposeFacts`), so the report "which deployed stacks violate the current policy" checks them again. Tightening the policy doesn't stop containers that already run.
- **`DeployStack`** under a policy (`Audit` or `Enforce`) gets:
  - `explicit_env_file`: the agent passes compose the same `--env-file` as during rendering. An `.env` created on the host outside the bundle (for example, by a container) no longer affects variable substitution;
  - in `Enforce`, `verify_paths`: the agent checks the paths again right before `up`.

#### Rehydrate

Rehydrate doesn't check the policy: after a reboot Docker brings these containers up with their configuration anyway, and a refusal would only leave the stack without secrets. `explicit_env_file` is sent here too.

#### Allowances

Every violation names the **allowance** (a `PolicyAllowance` string) that lifts it. An administrator or the environment's administrator sets allowances: for the whole environment (the allowlist) or for one stack. Every change is an audit entry.

| Allowance | What it allows |
|---|---|
| `privileged` | `privileged: true`, including in `post_start`/`pre_stop` |
| `host:<network\|pid\|ipc\|uts\|userns\|cgroup>` | a host namespace |
| `docker-socket` | mounting `docker.sock`/`containerd.sock` (even `:ro` is root), `use_api_socket` |
| `capability:<NAME>` | `cap_add`, except Docker's default capabilities and `NET_ADMIN`, `IPC_LOCK`, `SYS_NICE`, `SYS_RESOURCE` |
| `security-opt:<value>` | any `security_opt` except `no-new-privileges` |
| `device:<path>` | a host device; block devices and `/dev/mem` are never allowed in `Enforce` |
| `gpus`, `device-cgroup-rules`, `cgroup-parent`, `runtime:<name>`, `provider` | the corresponding keys; `provider` runs a program on the agent |
| `path:<path>`, `path-rw:<path>` | a bind mount outside the usual places, read-only or also for writing; for an environment it works as a prefix |
| `port:<number>` | publishing a host port below 1024 |
| `network:<name>`, `network-driver:<driver>` | a network outside the stack (e.g. `proxy`), a driver other than `bridge` |
| `volume:<name>`, `volume-driver` | a volume outside the stack; a driver other than `local`, or `driver_opts` (which is a bind mount of anything) |
| `foreign-container` | `container:…` in `network_mode`, `pid`, `ipc`, `volumes_from` |
| `propagation` | a `bind.propagation` other than the default |
| `label:<key>` | the labels `com.container-forest.*` and `com.docker.compose.*` |
| `container-name` | a `container_name` that doesn't belong to this stack: a name belongs to the environment's stack with the longest name that it equals or starts with followed by `-` (`web-db-1` with stacks `web` and `web-db` belongs to `web-db`). A new stack can't be created with a name that another stack's container already falls under |
| `key:<name>` | a service key the policy doesn't know |

#### Bind mounts

Paths are compared after normalisation, by segments, case-sensitively. Allowed without allowances:

- **the stack directory** (files from git). For writing, with a warning "move the data to the data directory". The root of the stack directory itself is allowed for writing only with a `path-rw:` allowance with the exact path;
- **the stack's data directory** `<DataRoot>/<stack>`, for reading and writing. `DataRoot` is set on the environment, `/var/lib/container-forest/data` by default. Docker creates missing directories itself;
- **system paths, read-only**: a list on the environment, by default `/etc/localtime`, `/etc/timezone`, `/usr/share/zoneinfo`, `/etc/locale.conf`, `/etc/default/locale`, `/etc/os-release`, `/etc/ssl/certs`.

**Protected paths** (`/`, `/bin`, `/boot`, `/dev`, `/etc`, `/home`, `/lib*`, `/proc`, `/root`, `/run`, `/sbin`, `/sys`, `/usr`, `/var/run`, `/var/lib/docker`, `/var/lib/containerd`, `/opt/container-forest`, `DataRoot` and other stacks' directories in it):

- apply to both reading and writing, and to the ancestors and descendants of these paths;
- the environment's allowlist doesn't open them, only a stack allowance with the **exact** path.

Two more rules:

- a source can't lie inside another source mounted for writing: that way a container can't plant a link at a path another service will mount;
- a source with a symbolic link in its path is rejected if the link lies where a container can write: the stack directory, the data directory, a source of this stack mounted for writing, a path opened by a `path-rw:` allowance. Links elsewhere (`/var/run` → `/run`, `/etc/localtime`) could only have been created by the host's root: they're part of the host, not a trap, and the agent doesn't check them before `up`.

`secrets`/`configs` files with `file:` must be in the stack directory.

#### An unrestricted stack

A stack that needs root on the host by design (`privileged`, `pid: host`, `docker.sock`) can be taken out from under the policy by a system or environment administrator with one switch (`Stack.PolicyUnrestricted`, `PUT /api/stacks/{id}/policy-unrestricted`, `forest stack policy unrestricted on`). Nothing is checked for such a stack, including file references in `ValidateComposeStep`; the agent still renders the model (an old agent only logs it), violations are logged as "not applied", and the report shows the stack marked *unrestricted*. `explicit_env_file` stays. The change goes to the audit log (`StackPolicyUnrestrictedChanged`), and the panel asks for confirmation before turning it on.

#### Exemption for endorsed content

Violations in a deployment whose content an administrator or this environment's administrator endorsed are written as warnings with that person's name, and the deployment goes on as in `Audit`. This doesn't lift the file reference and YAML tag checks in `ValidateComposeStep`: that's what an unrestricted stack is for. Endorsement follows ADR-0011: the commit is signed with the person's key, the key's scope includes the environment, or the person approved an unsigned commit by SHA. Pressing *Deploy* gives no exemption, since a deployment takes the branch head, which anyone could have changed. A stack without *Deploy only signed commits* has no exemption.

### 3. Per-stack SOPS keys

1. `AgeKey` gets an optional `StackId`: an environment key means secrets **shared** by the environment's stacks, a stack key means **secrets of that stack only**. The key is deleted with the stack (cascade).
2. A stack's deployment decrypts files with the environment's keys and **its own** stack's keys. Another stack's ciphertext copied into the directory won't be decrypted.
3. The git filter (ADR-0010) decrypts a file with a stack key for those who can deploy that stack: the environment's Editor and administrator, the stack's Editor, an administrator. With an environment key, as before, for Editors of the environment or any of its stacks.
4. An environment administrator or an administrator creates and deletes stack keys. The `.sops.yaml` rule for a stack key is `forest secrets rule add <path>=<environment>/<stack>`.
5. Real isolation exists only in `Enforce`: without the policy a stack's Editor can still read another stack's tmpfs through compose.

## Consequences

- A stack's Editor and a stolen signing key in an environment with `Enforce` are limited to their stack: no root on the host, no other stacks' data or secrets. The "Editor = root" warning in `architecture.md` becomes conditional.
- `.proto` changes compatibly: the `RenderStack` command, the `RenderedStack` result, the fields `DeployStack.explicit_env_file` and `verify_paths`. The policy needs a new agent, but an old one breaks nothing except the check itself.
- The agent needs the host root read-only (`/:/host:ro,rslave`). That gives no new rights: through `docker.sock` the agent is root anyway. Without it, paths outside `/opt/container-forest` aren't checked, and in `Enforce` a deployment with them fails.
- A deployment under a policy takes one more operation: rendering on the agent. The bundle is sent twice (up to 3 MiB).
- A deployment rejected at rendering has already delivered decrypted secrets to the agent's tmpfs for the duration of `compose config`. No containers start, and the commit signature is checked earlier.
- **Residual risk.** A still running container of an older stack version could swap a path for a link between the agent's check and the mount. For that, the old version must already be malicious under the policy and hit a window of milliseconds. The risk is documented, not closed.
- **Migration:**
  1. `Audit` on every environment.
  2. Collect the report for a week.
  3. Set up the allowlist and allowances.
  4. Turn on `Enforce`, starting with the least trusted environment.
- **Not covered:**
  - DoS through host resources;
  - escaping through a kernel vulnerability;
  - network access to services that are open anyway;
  - taking over ports from 1024 up that another stack released.
- **Next:** security events (policy changes, allowances on protected paths) in a separate Discord channel. Reworking the `forest secrets` commands after per-stack keys: the difference between `secrets add` and `secrets rule add` is unclear to a newcomer.
