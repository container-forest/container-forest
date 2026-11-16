# CLAUDE.md — container-forest

Manages Docker containers on several servers: a central aggregator (ASP.NET Core), an agent on every host, a web panel, a Discord bot and the `forest` CLI.

## Where to look

This file is loaded into every session. A component's conventions load on their own when you work with its files: [aggregator](src/Aggregator/CLAUDE.md), [agent](src/Agent/CLAUDE.md), [bot](src/DiscordBot/CLAUDE.md), [CLI](src/Cli/CLAUDE.md), [panel](src/Web/CLAUDE.md). **Read the other documents before changing** the part they describe:

| What you change | Document |
|---|---|
| Components, protocols, permissions, self-management, deployment | [docs/architecture.md](docs/architecture.md) |
| An architectural decision, or a "why is it like this" question | [docs/design/decisions.md](docs/design/decisions.md), [docs/adr/](docs/adr/README.md) |
| The deployment pipeline, stack operations, the stacks API | [docs/design/deployment.md](docs/design/deployment.md) |
| Webhooks, the CI deploy hook | [docs/design/deployment-triggers.md](docs/design/deployment-triggers.md) |
| Git clones, git connections, repositories | [docs/design/git.md](docs/design/git.md) |
| SOPS, age keys, the git filter, rehydrate | [docs/design/secrets.md](docs/design/secrets.md) |
| Commit signatures | [docs/design/signed-commits.md](docs/design/signed-commits.md) |
| Image updates by the aggregator: image policies, the image hook, update commits | [docs/design/image-updates.md](docs/design/image-updates.md) |
| Compose policy, environment administrators | [docs/design/compose-policy.md](docs/design/compose-policy.md) |
| Sign-in, sessions, users, API tokens, passkeys, Discord OAuth2 | [docs/design/auth.md](docs/design/auth.md) |
| The audit log | [docs/design/audit.md](docs/design/audit.md) |
| System settings, identity confirmation, aggregator backup and restore | [docs/design/backup.md](docs/design/backup.md) |
| Environments, container actions, logs, container events | [docs/design/containers.md](docs/design/containers.md) |
| Unmanaged stacks (compose projects started by hand on a host) | [docs/design/unmanaged-stacks.md](docs/design/unmanaged-stacks.md) |
| Resource usage metrics and alerts | [docs/design/metrics.md](docs/design/metrics.md) |
| SignalR and live updates | [docs/design/live-updates.md](docs/design/live-updates.md) |
| Notifications | [docs/design/notifications.md](docs/design/notifications.md) |
| The bot gateway, the bot token, `/dashboard` | [docs/design/discord-bot.md](docs/design/discord-bot.md) |
| A single panel screen | [docs/design/web-panel.md](docs/design/web-panel.md) |
| A new dependency, library pitfalls | [docs/dependencies.md](docs/dependencies.md), `src/<component>/CLAUDE.md` |
| What is already implemented | [docs/project-status.md](docs/project-status.md) |
| Contribution rules, repository layout | [CONTRIBUTING.md](CONTRIBUTING.md); local setup and CI: [docs/development.md](docs/development.md) |
| Installing and operating the system | [docs/installation.md](docs/installation.md) |
| Comparison with similar tools | [docs/comparison.md](docs/comparison.md) |

**What belongs in this file:** only what you could get wrong in a task touching any part of the project. A convention of one component goes into its `CLAUDE.md`, a description of a subsystem into `docs/design/`, a decision into an ADR. The root file must not exceed 150 lines (CI checks).

## Project state

- Version **0.x** (before 1.0): a minor release may contain breaking changes, and they must be described in the changelog (`feat!:` or `BREAKING CHANGE:`).
- **In production:** the system (aggregator, panel, agents, bot) runs real production infrastructure. Changes must stay compatible with deployed installations: EF Core migrations without data loss, `.proto` changes only compatible (agents and the bot are upgraded separately from the aggregator), new required settings with defaults.
- The repository layout is fixed in ADR-0001. CI (GitHub Actions) builds and tests every PR.
- **Not there yet:** 2FA and OIDC sign-in, live log streaming (`follow`; the tail of a log is read through the aggregator and the agent), rolling back a deployment from the panel (the agent keeps `.prev`, but there is no rollback command), exec/attach.

## Working rules for Claude Code

- `README.md` is the public page for visitors of the repository: don't add technical details or contribution rules there, link to `docs/` and `CONTRIBUTING.md`.
- Documentation, identifiers, code comments and commit messages are in English.
- Answer directly, at the implementation level, with working code rather than conceptual overviews.
- **Don't break the security invariants (below).** If a task requires breaking one, stop and ask.
- Change `.proto` contracts only compatibly: new fields and new `oneof` cases, no renumbering or removal (a removed field goes to `reserved`). The package has no version (ADR-0002), so a breaking change means upgrading the aggregator and every agent at once: avoid it. Exception: a contract is a draft (it may change incompatibly) until the first aggregator, agent or bot code using it is merged into `main`.
- Don't add a dependency silently: name it, explain why it's needed, check its license (no GPL or AGPL in code we link against) and add a row to [docs/dependencies.md](docs/dependencies.md).
- Commits follow Conventional Commits (`feat(agent): ...`), no direct commits to `main`. A maintainer reviews every PR (`.github/CODEOWNERS`); before merging, check that the PR's base is `main`, not another PR's branch.
- An architectural decision that changes something in the [decisions](docs/design/decisions.md) gets an ADR in `docs/adr/`.
- Changed behaviour described in `docs/design/` or a nested `CLAUDE.md`: update the description in the same PR.

## Security invariants

@docs/security-invariants.md

## Key decisions

The full list with reasoning: [docs/design/decisions.md](docs/design/decisions.md).

- Aggregator layers: `Domain ← Application ← Infrastructure ← Api`. `src/Contracts` holds only `.proto` files; each consumer generates its own code.
- The agent opens the gRPC stream to the aggregator itself. The bot also talks to the aggregator over gRPC; the panel and the CLI use the REST API.
- Git and SOPS live in the aggregator; the agent pulls images. Authorization happens only in the aggregator, as a Decorator around commands.
- Restart, redeploy, redeploy with pull and down are separate operations.
- The database is SQLite. Secrets in the database are encrypted with ASP.NET Core Data Protection.

## Rejected alternatives (don't propose again without new arguments)

- **A FUSE volume with secrets.** FUSE inside a container needs `SYS_ADMIN` and `/dev/fuse` on every container. The right form is a Docker volume plugin, but even that has little advantage over injection at start, since most applications don't reread their config. Existing plugins (`docker-volume-keywhiz`) are abandoned. There is no decent FUSE binding for .NET.
- **The aggregator connects to hosts' Docker API over TCP.** Replaced by agents.
- **The agent clones git and decrypts secrets itself.** Keys and credentials would spread across hosts.
- **Blazor.** The panel is a React SPA served separately.
- **Next.js.** A dashboard behind a login doesn't need SSR; an extra Node server and the server/client components confusion.
- **Vue / Angular.** Considered (Vue has the lowest entry barrier, Angular is closest to C#); React was chosen.
- **Redux.** Server state lives in TanStack Query, URL state in the Router.
- **Our own sign-in implementation.** Password hashing and sessions come from ASP.NET Core; the project's value is in the authorization layer, not in sign-in.
- **Complex Portainer BE-style RBAC with explicit denies.** The effective role is the higher of the environment role and the stack role.

## Shared .NET conventions

- Tests are xUnit v3 on Microsoft.Testing.Platform (enabled in `global.json`): `dotnet test --solution ContainerForest.slnx`. Test projects live in `tests/`, mirroring `src/`.
- Configuration through options + `AddEnvironmentVariables(prefix: "CF_")`, `ValidateOnStart()`. Nested sections use `__` (`CF_AGENT__CONTROLLERURL`).
- Write to a gRPC stream only through the wrapper with a `SemaphoreSlim`: gRPC forbids concurrent writes.
- Before pushing, run the steps from [CI](docs/development.md#ci): compiler warnings are errors in CI, and the generated OpenAPI document and clients must be up to date.

## Shared pitfalls

- Stack names: `^[a-z0-9][a-z0-9_-]*\z`, up to 63 characters (a compose requirement and a guard against path traversal). Use `\z`, not `$`, in validation regexes: `$` lets a trailing `\n` through. Every `StackFile.Path` is relative, has no `..`, and stays inside the stack directory after `Path.GetFullPath`.
- xUnit v3 with the `xUnit1051` analyzer requires passing `TestContext.Current.CancellationToken`; in CI it's a build error.
- Container logs can contain secrets; masking in the audit log doesn't help with what an application prints to stdout itself.
- A developer machine may lack the .NET SDK: build and test in a `mcr.microsoft.com/dotnet/sdk:10.0` container with the repository mounted at `/src` (agent tests involving `/bin/sh` and file permissions then run as on Linux).

## Deferred (deliberately not now)

- Moving to .NET 11: after its release, once every dependency works with it or has been updated.
- mTLS between the agent and the aggregator: a stretch goal.
