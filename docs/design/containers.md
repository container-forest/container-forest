# Environments, containers and logs

Aggregator paths are relative to `src/Aggregator`.

## Access

- **Environments** are created and deleted only by administrators. An administrator or the environment's administrator (ADR-0012) issues enrollment tokens, revokes the agent and changes the environment.
- **Visibility:** environment and container lists are filtered by `AccessPolicy`. An inaccessible environment is a 404, not a 403, and no request reaches the agent.
- **Finding a container** (`ContainerLookup`) works as in the docker CLI: the full id, then the exact name, then a unique id prefix (an ambiguous prefix is a 409).

## Container actions

They go only through `ContainerCommandService`:

1. An environment the user can't see is a 404 before reaching the agent, so its existence isn't revealed.
2. `ListContainers` gives the state and the stack label.
3. `AuthorizedCommand` checks the permission on the stack, or on the environment for containers outside managed stacks.
4. `ContainerCommand` checks the state and sends the command.

**Removal** (`RemoveContainerCommand`, `POST .../containers/{c}/remove`) is `docker rm` without force and without volumes, only for a stopped container (created, exited, dead), with the Deploy permission (Editor) on the stack or the environment. Other states are a 409. Details: [unmanaged stacks](unmanaged-stacks.md).

**System containers can only be started:** stop/restart would cut the connection the command travels on. These are the containers of system stacks and containers labelled `com.container-forest.system: "true"` (the aggregator, the panel's Caddy, the agent in `deploy/*.yaml`). The latter are deployed by hand and have no `Stack` record, so the label is the only way to recognise them.

- The agent passes the label in `ContainerInfo.system` (`ContainerInfoMapper.SystemLabel`), the aggregator in `ContainerSnapshot.IsSystem`.
- `ContainerCommand` refuses (409) before reaching the agent.
- `ContainerResponse.isSystem` (the label or a system stack) hides the panel's buttons.
- The bot shows no *Restart* button under notifications about such containers (`ContainerDetails.system`), and `/stop` or `/restart` get `FAILED_PRECONDITION`.

**Agent responses:** a command's result arrives in `AgentSession.Complete` by `request_id`, progress (`CommandProgress`) in the `onProgress` callback, and log batches (`LogBatch`) in the same request's `onLogs` callback.

## Container events

**On the agent:** while the session lives, `ContainerEventForwarder` reads Docker's event stream (`IDockerGateway.WatchContainerEventsAsync`: `System.MonitorEventsAsync` filtered by `type=container`, through a `Channel` to keep the order).

- **State after an event:** taken from `FindContainerAsync`. For `destroy` there's none, and a failed lookup doesn't cancel the event.
- **The exit code** for `die` comes from the `exitCode` attribute (`DockerDotNetGateway.ExitCodeOf`): the status after the event no longer has it if the restart policy has already started the container again.
- **The filter** (`ContainerEventFilter`) lets through only state changes: create, start, restart, stop, die, pause, unpause, destroy, rename, oom, update, `health_status*`. `exec_*` carry a command line that may contain a secret, and attach/resize/copy are noise.
- **Failures:** if Docker is unreachable or closes the stream, retry in 5 s. Events while disconnected are lost: panels reread everything after the agent reconnects anyway.

**On the aggregator:** a `ContainerEvent` goes to `ContainerEventCoalescer`: at most one `ContainersChanged` per environment per 500 ms, since `compose up` produces a create and a start for every container and each reread is a `ListContainers` on the agent. `ContainerIncidentWatch` takes the event's content (action, container state) for notifications.

## Logs

There's no live stream (`follow`) yet: logs are read once.

**On the aggregator** (`ContainerLogService`, `GET /api/environments/{id}/containers/{container}/logs`):

- `StreamContainerLogs` with `tail_lines` (1–5000, 200 by default) and `since`; the aggregator collects batches until the `CommandResult` and returns one response. For new lines, the panel asks again with `since` = the time of the last line.
- The aggregator keeps only the last `tail` lines (`ContainerLogTail`), even if the agent sends more.
- The permission is `Permission.ViewLogs` on the container's stack, or on the environment for a container outside managed stacks. An invisible container is a 404.

**On the agent** (`StreamContainerLogsHandler`):

- **Reading:** `GetContainerLogsAsync` with `Timestamps = true`. Docker.DotNet inspects the container itself and returns a single stream, all as stdout, for a container with a TTY.
- **Parsing** (`ContainerLogParser`): lines are assembled separately for stdout and stderr, since chunks split lines and UTF-8 characters. The RFC 3339 time with nanoseconds is cut off into `timestamp_unix_ms` (a line without a time gets 0), UTF-8 is decoded replacing invalid bytes, and a line is truncated to 16 KB with "…".
- **Sending:** `LogBatcher` sends `LogBatch` messages, then a successful `CommandResult`. The handler sends `LogBatch` through `ILogSink`, implemented by the same `StreamCommandProgress`, so the `IAgentCommandHandler` signature didn't change.
- **`since`** goes to Docker as `seconds.nanoseconds` and includes the boundary: lines with the same time come back again, and dropping them is the panel's job.
- **`follow = true`** is `ERROR_CODE_UNSUPPORTED`.
- **Timeout:** the 30 s budget covers only opening the stream; `CancelRequest` aborts reading.
- **Security:** log content is never written to the agent's log.

**Docker's behaviour:** a line without a trailing `\n` is returned only once the container finishes it or exits. Docker itself splits a line longer than 16 KB into partial entries, and `tail` counts entries.
