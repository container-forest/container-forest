# Notifications

When exactly an event is sent is described in the comments of `ContainerIncidentWatch` and `AgentConnectivityWatch`; the defaults are in `NotificationOptions`. Aggregator paths are relative to `src/Aggregator`.

## Delivery channel

Notifications reach Discord through the live bot, not a channel webhook: the bot holds the `WatchNotifications` server stream and reports each one with `ReportDelivery` (`bot.proto`).

- **Why:** webhook URLs (effectively secrets) aren't stored anywhere, and the bot adds buttons to the message, whose clicks go through the ordinary `ControlContainer` or `GetContainerLogs` with the permissions of whoever clicked.
- **The price:** without a connected bot, notifications aren't delivered. They stay `Pending` and go out once it connects; those older than an hour become `Expired`.

## Domain

`Domain/Notifications`:

- `NotificationChannel`: a Discord channel.
- `NotificationRule`: which events, from what scope: the whole system, an environment or a stack (`Matches`).
- `NotificationEvent`: one event of exactly one kind.
- `Notification`: a history record: Pending → Delivered | Failed | Expired. The history has no foreign keys to rules and channels so it survives their deletion.

**System-wide events** (`BackupFailed`, `SettingsChanged`, `AccountSecurityChanged`) have no environment: `NotificationEvent.EnvironmentId` is empty, so only system-wide rules receive them. The bot gets `environment_name` set to `System` rather than empty: an older bot would show the environment field and fail to build the embed. `SystemNotifier` creates these events, and its failures are only logged.

**`ImageUpdateFailed`** and **`ImagePinned`** (ADR-0015) are stack events from `ImageUpdateService` and `ImagePolicyService`: the first when an image policy stops working (once until the next successful check; an unreachable registry from the third attempt in a row), the second when a person or the hook pins an image, and when it's unpinned. In `bot.proto` they're `NOTIFICATION_KIND_IMAGE_UPDATE_FAILED` and `NOTIFICATION_KIND_IMAGE_PINNED`; an older bot shows them as "Notification".

**`AccountSecurityChanged`** is sent by `UserManagementService` when an administrator grants administrator rights, creates an administrator or resets a password: each of these gives a way to sign in. The text says who did it to whom, without the password. Changing your own password and other user actions don't create the event. Existing rules don't receive it until it's added to a rule.

## Aggregator

- **Dispatcher:** an event source calls `NotificationDispatcher.DispatchAsync(event, summary)`. It creates one `Notification` per channel (from the most specific matching rule), saves it and wakes the bot's stream through `NotificationFeed`.
- **Deployments:** through `IDeploymentObserver` (`DeploymentNotifier`), which `DeployPipeline` calls after saving the result. The observer logs its own errors and never throws.
- **Details:** `Notification.Details` is a snapshot of the event's details (`DeploymentNotificationDetails`), stored as a JSON column (`NotificationDetailsConverter`), so a notification delivered after the bot reconnects is the same one.
- **The channel and the environment's name** are taken at sending time: a moved channel also gets old notifications, and those for a deleted or disabled channel become `Failed`.

## Bot

- **Stream:** `NotificationWatcher` (a hosted service) waits for Discord's `Ready`, holds the stream through `INotificationSource` (`GrpcNotificationSource`) and reconnects with backoff. The channel sends HTTP/2 keepalive pings because the stream may be silent for hours.
- **Posting:** `INotificationPoster` (`DiscordNotificationPoster`, an embed with `AllowedMentions.None`) posts each notification and reports the result.
- **Refusals:** a Discord refusal (no channel, missing permissions) is `delivered = false` with a hint for the panel. A failed report is only logged: the aggregator sends the notification again on the next stream.

## Settings in the panel

`NotificationSettingsService`, `Api/Contracts/NotificationContracts.cs`.

- **Access:** channels, rules and history are for administrators only, since people without any role may be able to read a Discord channel.
- **Discord ids** are strings in JSON: JavaScript rounds 64-bit numbers.
- **A rule's events** are a list of individual kinds; in the domain they're `NotificationEventKinds` flags. An empty list is a 400.
- **A rule's scope:** a stack rule may omit the environment, and a wrong one is a 400. The scope can't change (delete and add a new rule); only the events and the enabled flag can.
- **Deleting a channel** removes its rules (cascade); the history stays with `channelName = null`.

## Agent offline

`AgentConnectivityWatch` (singleton), `AgentConnectivityNotifier` (scoped). `AgentGatewayService` informs the watch after `registry.Open` and after `registry.Remove` of the current session.

- Another disconnect while waiting doesn't move the downtime.
- A deleted environment isn't notified.
- The event has no stack: stack rules don't receive it.

## Crashed and unhealthy containers

`ContainerIncidentWatch` (singleton), `ContainerIncidentNotifier` (scoped). `AgentGatewayService` passes each `ContainerEvent` as a `ContainerObservation`.

- **The event sequences** in the class's comment were checked on Docker 29. `docker kill` without stop also counts as a crash.
- **Exit code:**
  - Taken from `ContainerEvent.exit_code`, or, from an older agent, from Docker's status after the event (`Exited (N)` or `Restarting (N)` for a container with a restart policy). A container Docker has already started again is then notified without a code.
  - Code 0 isn't notified, including a job restarted by an `always` policy.
  - A code above 128 is death by signal 128 + N, named in the text (`SignalName`).
- **OOM:** the text "was killed: out of memory" instead of code 137, `ContainerNotificationDetails.OutOfMemory`. `bot.proto` has no field for OOM: the bot only sees it in the text.
- **Unhealthy:** an unhealthy suppressed by the repeat interval has no "healthy again" of its own. In `bot.proto` it's `NOTIFICATION_KIND_CONTAINER_HEALTHY`; an older bot shows it as "Notification" without buttons.
- **A container** is counted by name, since recreating it changes its id.
- **The event's stack** is the environment's managed stack named like the compose project. Another project, or a container outside compose, is an environment event that stack rules don't receive.
- **Details:** `ContainerNotificationDetails` (a JSON column, `ContainerDetails` in `bot.proto`).

## Buttons under a notification

`ContainerButton`, `NotificationModule`. `DiscordNotificationPoster` adds them to `ContainerDied` and `ContainerUnhealthy`.

- **For which containers:** *Logs* always. *Restart* except for containers that can only be started (`ContainerDetails.system` in `bot.proto`: the `com.container-forest.system` label from the agent's `ContainerInfo.system`, or a system stack, `ContainerNotificationDetails.IsSystem`).
- **`custom_id`:** `ntf:<rs|lg>:<environment in base64url>:<container name>`; a name that's too long means a notification without buttons. The bot is stateless, so buttons work under old messages too. Ids are `CompactGuid` (shared with `/dashboard`).
- ***Logs*** (`ContainerLogsCommand`, `GetContainerLogs` in `bot.proto` → `ContainerLogService`, permission `ViewLogs`) shows the end of the log that fits in one message (`ReplyFormatter.WithOutput`), without ANSI colours and control characters. Logs can contain secrets, so the reply, like every bot reply, is visible only to whoever clicked.
- ***Restart*** runs `RestartContainerCommand` on behalf of whoever clicked: the aggregator checks permissions, and an unlinked account gets a `/link` hint. Only they see the reply: the component is deferred with `DeferLoadingAsync(ephemeral)`, since a plain `DeferAsync` on a component would make the reply an edit of the notification itself in the channel.
- **The *Last action* field:** the bot marks a successful restart in the notification itself (`NotificationEmbed.WithRestart`: who and when), so others in the channel don't restart the container again. `<@id>` in an embed pings nobody, the next restart replaces the line, and a failed edit (the message was deleted) only goes to the log.
