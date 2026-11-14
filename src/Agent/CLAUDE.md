# Agent: conventions

Command handlers, Docker and compose. What the agent receives from the aggregator: [deployment](../../docs/design/deployment.md), [secrets](../../docs/design/secrets.md), [compose policy](../../docs/design/compose-policy.md), [containers and logs](../../docs/design/containers.md).

## Contract

`src/Contracts/agent/agent.proto` is the single source; don't copy it here. Deferred: statistics for graphs, exec/attach, a separate rehydrate command (for now it's another `DeployStack`).

## Connection and commands

- Reconnects use exponential backoff (up to 60 s) with jitter; if a session lived longer than a minute, the attempt counter resets. Heartbeats every 15 s.
- Commands are dispatched by `ControllerMessage.BodyCase` to an `IAgentCommandHandler`; handlers get an `ICommandProgress` to stream output.
- Every command except `CancelRequest` gets exactly one `CommandResult` with the same `request_id`, or the aggregator waits until its timeout. A command without a handler (including a `oneof` case the agent doesn't know, which parses as `BodyCase.None`) gets `ERROR_CODE_UNSUPPORTED`. `CommandError.message` is shown in the panel: readable text without exception details; the details go to the agent's log.
- Cancellation: `CommandDispatcher` keeps `RunningCommands` (request_id → a token linked to the session's token). `CancelRequest` cancels the command's token and the handler ends with `ERROR_CODE_CANCELLED` (compose is killed, the Docker API call is aborted); an unknown id is ignored, and a duplicate request_id of a running command gets `ERROR_CODE_INVALID_ARGUMENT`. A command is registered synchronously before its first `await`, and the session reads the next message only after that, so a `CancelRequest` can't overtake its target. A cancelled deployment doesn't delete the secrets' `.prev`.

## Docker

- The agent uses the `Docker.DotNet.Enhanced` fork (Testcontainers), not the abandoned original. The client is `new DockerClientBuilder().Build()`: the endpoint is resolved as in the docker CLI (`DOCKER_HOST` → the current context → the default socket). The client's own timeout (100 s) is off because the aggregator allows stop timeouts of up to 5 minutes; every call has its own budget in `DockerDotNetGateway`.
- `timeout_seconds = 0` in Stop/Restart means "kill at once" (the aggregator accepts 0), not Docker's default.
- `container_id` is checked against `^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z` before calling the Docker API: the id becomes part of the request path.
- The agent's credentials are `<DataDirectory>/agent-credentials.json`, written atomically, mode 0600 from creation on Unix. The hostname for `Enroll` is `Environment.MachineName`; in a container, `hostname:` in the agent's compose file sets it (there is no separate environment variable).
- The agent's image is `src/Agent/Dockerfile`, built from the repository root. The base is `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04, not Debian): `docker-ce-cli` and `docker-compose-plugin` are installed from Docker's apt repository, with the distribution taken from `ID` in `/etc/os-release`. `WORKDIR /app` is required: without it the content root is `/` and `appsettings.json` isn't read. The agent runs as root because the Docker socket's gid differs between hosts.

## Compose and stacks

- Run `docker compose` through `ProcessStartInfo.ArgumentList` (no shell). Pass env secrets in `psi.Environment`, never in an `.env` file.
- Stack operations: `restart` = `docker compose restart`; `redeploy` = `docker compose -p <stack> -f compose.yaml up -d --remove-orphans --pull missing --wait`; `redeploy with pull` is the same with `--pull always`. The agent adds the global `--ansi never --progress plain` so every line of output reaches the deployment log as text. Only `ComposeInvocation` builds arguments (factories `Up`/`Restart`/`Down`); `DockerComposeCli` starts the process without a shell, with the `Agent:ComposeTimeout` timeout (15 minutes), and kills the process tree on cancellation. The agent's `CF_*` variables are not passed to compose: a compose file could substitute them into a container.
- One operation per stack at a time (`StackLocks`): a second one gets `ERROR_CODE_BUSY` rather than waiting. `RestartStack` and `DownStack` for a stack without a directory get `ERROR_CODE_NOT_FOUND` (compose silently returns 0 for a project that doesn't exist).
- In a `DeployStack` bundle the stack's compose file is always `compose.yaml` at the root of the stack directory, whatever it's called in the repository (`StackBundle.ComposeFileName`). The agent passes it with `-f compose.yaml`, so other compose files in the directory (including `compose.override.yaml`) aren't mixed in.
- Run `RestartStack` and `DownStack` as `docker compose -p <stack> restart|down --timeout <N>` **without `-f`**, with `StacksDirectory` as the working directory rather than the stack directory. That way compose doesn't read the file and doesn't require env variables that aren't on disk; with `-f`, or without `-p` in the stack directory, the command fails on required `${VAR:?}` variables (checked with Compose 5.5.1). The env stays in the containers' configuration.
- The agent finds unmanaged stacks (ADR-0016, [description](../../docs/design/unmanaged-stacks.md)) itself in `Agent:UnmanagedStackRoots` (`UnmanagedStackFinder`); the aggregator names them only by name, and the agent never accepts a path from the aggregator for them. Up and pull run with `-p`, `--project-directory`, `-f` and `--env-file` taken from the containers' labels; restart and down run as for managed stacks.

## Stack files and secrets

- The stack directory is never replaced (ADR-0004): containers mount paths in it, and replacing the directory would leave them on the old copy with their data. `StackFileStore` prepares each file as `<name>.tmp` and renames it over the target; files of the previous bundle that aren't in the new one are deleted using the manifest `<StacksDirectory>/<name>.manifest.json` (kept outside the stack directory so a container can't rewrite it). Files created by containers aren't touched. The previous bundle is copied to `<name>.prev`.
- The agent runs as root, and a container could create a link to `/` in a mounted directory: before writing, copying or deleting, the agent checks that there are no symbolic links between the stack directory and the file. A directory on the host where the bundle has a file is a `StackFileConflictException` (`ERROR_CODE_COMMAND_FAILED`) before any change.
- Files with `StackFile.secret = true` are written to `/run/container-forest/<stack>/<path>` (the stack's directory is 0700; it's replaced as a whole, `.tmp` → current → `.prev`, since containers don't write there), and the stack directory holds a symbolic link to each at the same relative path. The compose file in the repository refers to a secret by an ordinary relative path (`file: ./secrets/db_password`), and Docker resolves the link on the host: paths on the host and in the agent are the same. Secret content never reaches the disk; after a host reboot the links dangle until rehydrate. The `.prev` with secrets is deleted right after a successful `compose up`, and `DownStack` deletes the stack's secrets. On Windows (development only) secret files may be written straight into the stack directory, with a warning in the log.

## Pitfalls

- Developing on Windows: Docker.DotNet connects through the named pipe of the current Docker context (`npipe://./pipe/dockerDesktopLinuxEngine` with Docker Desktop). With Docker stopped, the connection waits 10 s and ends with `TimeoutException`. There is no `/proc/.../boot_id`, so the boot id is the system start time (`UptimeBootIdProvider`).

## Library notes

Why each library was chosen: [docs/dependencies.md](../../docs/dependencies.md).

- **Docker.DotNet.** Starting a running container or stopping a stopped one returns 304, which the library reports as `false`: for the agent that's success. `DockerApiException.Message` includes internal text and the raw body: show only the `message` field from `ResponseBody`. Docker being unreachable surfaces as `TimeoutException` (Windows named pipe), `HttpRequestException` or `SocketException`; all map to `ERROR_CODE_DOCKER_UNAVAILABLE`. `ContainerListResponse.Created` is a `DateTime`: force its `Kind` to UTC before converting to Unix milliseconds.
- **Logs.** `GetContainerLogsAsync` in the fork inspects the container itself to learn `Config.Tty` (the original package took a `tty` parameter), so it costs an extra request and "no such container" comes from the inspect.
- **gRPC.** `AgentCommandHandler<TCommand>` finds its `oneof` case through the protobuf descriptor by type, relying on `BodyOneofCase` values matching field numbers. Every command runs as its own task so a slow stop doesn't hold up heartbeats. Besides heartbeats, the channel sends HTTP/2 keepalive pings (`SocketsHttpHandler.KeepAlivePingDelay`) to notice a dead connection while writes still fit in buffers.
