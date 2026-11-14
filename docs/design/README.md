# Subsystems

How the implementation works: classes, rules and non-obvious decisions. The system overview is in [architecture.md](../architecture.md), discussed decisions in [adr/](../adr/README.md), code conventions in each component's `CLAUDE.md`.

- [Decisions and their reasoning](decisions.md)
- [Stack deployment](deployment.md): the pipeline, restart and down, the stacks API.
- [Webhooks and deploy hooks](deployment-triggers.md): webhooks, the CI deploy hook.
- [Git and git connections](git.md): clones, connections, repositories.
- [Secrets: SOPS, the git filter, rehydrate](secrets.md)
- [Signed commits](signed-commits.md): endorsing content with signatures.
- [Image updates by the aggregator](image-updates.md): image policies, the image hook, update commits and their endorsement.
- [Compose policy and environment administrators](compose-policy.md)
- [Authentication, users, tokens](auth.md): sign-in, users, tokens, passkeys, Discord OAuth2.
- [Audit log](audit.md)
- [System settings and aggregator backup](backup.md): settings sections, identity confirmation, backup and restore.
- [Environments, containers and logs](containers.md): environments, container actions, events, logs.
- [Unmanaged stacks](unmanaged-stacks.md): compose projects started by hand on a host: discovery, operations, permissions.
- [Live updates](live-updates.md): SignalR.
- [Notifications](notifications.md): notifications and their buttons.
- [Discord bot: gateway and menu](discord-bot.md): the bot gateway, its token, /dashboard.
- [Panel screens](web-panel.md): individual panel screens.
