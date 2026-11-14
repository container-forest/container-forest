# Decisions and their reasoning

A new decision that changes something in this table gets an [ADR](../adr/README.md).

| Decision | Why |
|---|---|
| Aggregator layers `Domain ← Application ← Infrastructure ← Api`, no SharedKernel; `src/Contracts` holds only `.proto` files and each consumer generates its code (ADR-0002); the web panel gets its types from a committed OpenAPI document (ADR-0001, ADR-0002) | The only thing shared between applications is the protocol; changes to the HTTP contract show in the PR diff |
| The agent on each host opens a gRPC bidi stream to the aggregator itself | No inbound ports on hosts, the Docker API isn't exposed to the network, works behind NAT |
| gRPC rather than SignalR for agents and the bot | A strict contract in `.proto`, natural log streaming, the agent could in principle be rewritten in another language |
| Request/response on top of the stream with `request_id` + `TaskCompletionSource` | A gRPC stream has no request→response semantics |
| SOPS is decrypted only in the aggregator; a separate age key pair per environment | The aggregator already has root-equivalent control over every host; sending keys to hosts would widen the attack surface for no gain. A key per environment gives cryptographic isolation between servers |
| The aggregator fetches git repositories (LibGit2Sharp, HTTPS + a read-only token) | The aggregator needs the content to decrypt it; the agent needs neither git credentials nor keys |
| The token and allowed environments belong to the git connection, not the repository ([ADR-0006](../adr/0006-git-connections.md)) | Otherwise an Editor of one environment could deploy another environment's private repository |
| The agent pulls images straight from the registry | Sending gigabytes through the aggregator makes no sense; the aggregator passes registry credentials to the agent in a temporary `DOCKER_CONFIG` on tmpfs |
| The agent runs the `docker compose` CLI rather than reimplementing compose over the Docker API | Reimplementing compose is a huge amount of work |
| Stack directories are mounted into the agent at the same path as on the host | The Docker daemon on the host resolves bind-mount paths |
| File secrets in `/run/container-forest` (the host's tmpfs) | They never reach the disk; the price is a rehydrate after a reboot |
| Env secrets are a deliberate trade-off | Docker stores env in the container's configuration on disk (`docker inspect`). Everyone does this, Portainer included; it's documented, not hidden |
| Commands to an offline agent aren't queued | A `restart` executed hours later is worse than an error |
| Authorization only in the aggregator, as a Decorator around commands | One check for the panel and the bot; it can't be bypassed through another interface |
| The CLI gets its token through a device flow with a loopback signal ([ADR-0008](../adr/0008-cli.md)) | Works with any way of signing in to the panel and over SSH; the token never passes through the browser |
| Endorsing stack content with SSH commit signatures verified by the aggregator ([ADR-0011](../adr/0011-signed-commits.md)) | Push access equalled root on the hosts and access to plain-text secrets, including for a compromised git server |
| The aggregator itself commits new images to the stack's repository according to an image policy, not CI; an update record on top of endorsed content endorses the commit, not the aggregator's key ([ADR-0015](../adr/0015-image-updates.md)) | CI needs neither write access to the deployment repository nor a signing key, and every stack commit names an exact image, so rolling back the files rolls back the image too |
| Stack isolation: the aggregator checks the compose policy against the model the agent renders ([ADR-0012](../adr/0012-stack-isolation.md)) | Without a policy a stack's Editor is root on the host; raw YAML in the aggregator would mean parser discrepancies |
| Personal API tokens with restrictions on top of roles, checked in `AccessPolicy` ([ADR-0007](../adr/0007-api-tokens.md)) | Tokens leak more often than sessions; a filter at the HTTP level doesn't cover lists and public environments |
| Secrets are plain text in a clone through the CLI's git filter and standard sops files in git ([ADR-0010](../adr/0010-git-secrets-filter.md)) | No personal age keys or manual steps, and the repository isn't tied to the product |
| Settings of optional features are typed sections in the database; an environment variable takes precedence ([ADR-0013](../adr/0013-system-settings.md)) | Features are turned on from the panel without access to the host, and whoever wants to pins the value in the environment; a sensitive change needs a fresh password or passkey |
| The aggregator backs itself up: an archive encrypted to an offline key, in an S3-compatible bucket ([ADR-0014](../adr/0014-aggregator-backup.md)) | A consistent SQLite snapshot, visible state and failure notifications; a bucket at another provider survives losing the host |
| Compose projects started by hand are unmanaged stacks in roots the host's administrator opened to the agent; the aggregator names a stack only by name ([ADR-0016](../adr/0016-unmanaged-stacks.md)) | Pull, up, restart and down without SSH, and the aggregator can't make the agent run a compose file outside those directories |
| One version for the whole system, releases through a release-please release PR, `latest` only for the last release (docs/development.md, "Releases") | The number shows whether the agent, the bot and the CLI are compatible with the aggregator; you decide when to release, and nodes on `latest` never get an untested `main` |
| Three separate operations: restart / redeploy / redeploy with pull (+ down separately) | Portainer merges them into one button, the main source of complaints about it |
| The web panel is a static SPA in Caddy, which is the single entry point to the system ([ADR-0005](../adr/0005-caddy-entry-point.md)) | Same origin → a `SameSite=Strict` cookie and WebSocket without CORS; certificates and long gRPC streams without manual configuration |
| React + Vite + TanStack Router + TanStack Query | Typed routes and search params, loaders integrated with the Query cache |
| UI: shadcn/ui on Tailwind CSS and Radix UI, Material Symbols icons | Tailwind is needed anyway; shadcn components live in the repository and are adapted to our screens, and Radix provides accessibility (focus, keyboard, ARIA). Mantine / MUI / Ant Design were rejected: their own look and styles, and MUI and Ant also bring CSS-in-JS and their own forms, which clash with React Hook Form |
| The Discord bot is a separate .NET application talking to the aggregator over gRPC | The bot's service token + the user's Discord ID (on-behalf-of); the aggregator checks permissions |
| Secrets in the aggregator's database are encrypted with ASP.NET Core Data Protection ([ADR-0003](../adr/0003-secrets-at-rest.md)) | Key rotation and authenticated encryption out of the box; our own ciphertext format would have to be written and tested by us |
| Registry credentials belong to an environment, and the agent receives only those the stack's images need | An environment's Editor is effectively root on the host and can read everything the agent receives; other environments' credentials never reach it |
| SQLite | Supporting PostgreSQL as well means two sets of EF Core migrations |
| System stacks (aggregator, agent) are updated through a one-off helper container | An agent running `compose up` on its own container kills itself mid-command |
