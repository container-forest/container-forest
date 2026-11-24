# Installation

Running container-forest on your own servers: the aggregator with the panel, an agent on every server, optionally the Discord bot, then backups and upgrades. For a five-minute version see the [Quick start](../README.md#quick-start).

## What you need

- **A central host** for the aggregator and the panel: Docker with Compose, a DNS name pointing to it, and ports 80 and 443 reachable from the internet (Caddy gets a Let's Encrypt certificate) and from your servers.
- **Every server you want to manage:** Docker with Compose. Servers only make outbound connections to the central host, so they can sit behind NAT and need no open ports.
- Images are published for amd64 and arm64.

The central host can be one of the managed servers too: give it an agent like any other.

## 1. Aggregator and panel

The two run on one host from [deploy/aggregator/compose.yaml](../deploy/aggregator/compose.yaml). The panel's image is Caddy: it serves the panel, proxies `/api`, `/hubs` and gRPC from agents and the bot to the aggregator, and handles TLS. The aggregator publishes no ports.

```
Internet :443 → container-forest-web (Caddy)
                  ├─ /             → panel files
                  ├─ /api, /hubs   → aggregator:5080
                  └─ gRPC          → aggregator:5081 (h2c) ← agents and the bot
```

```sh
mkdir -p /srv/container-forest && cd /srv/container-forest
curl -fsSLO https://raw.githubusercontent.com/lepterion/container-forest/main/deploy/aggregator/compose.yaml
touch .env && chmod 600 .env
$EDITOR .env
docker compose up -d
```

`.env` is never committed and should be readable by root only. Its variables:

| Variable | Required | Meaning |
|---|---|---|
| `CF_DOMAIN` | yes | The public name, e.g. `forest.example.com`. The panel, its API and gRPC for agents and the bot all use it. |
| `CF_BOOTSTRAP__ADMINUSERNAME`, `CF_BOOTSTRAP__ADMINPASSWORD` | first start | The first administrator (password of at least 12 characters), created when the database has no users. Remove both once you have signed in. |
| `CF_BOTGATEWAY__TOKENHASHES__0` | with the bot | SHA-256 of the bot's service token, never the token itself. See [Discord bot](#4-discord-bot-optional). |
| `CF_DISCORDAUTH__CLIENTID`, `CF_DISCORDAUTH__CLIENTSECRET` | no | Sign-in and account linking with Discord: the OAuth2 client of a Discord application (the bot's will do) with the redirect `https://<CF_DOMAIN>/api/auth/discord/callback`. The client secret is as sensitive as the bot token. |
| `CF_BACKUP__*` | no | Fixes a backup setting so the panel can't change it. See [Backups](#5-backups). |
| `CF_SIGNIN__REQUIRETWOFACTORFORADMINISTRATORS` | no | `true`: every administrator must have an authenticator app or a passkey; one with neither can only set one up after signing in. Otherwise *Users* has the switch. |

The whole file is passed to the aggregator, so a variable that isn't in it stays unset.

Open `https://<CF_DOMAIN>`, sign in as the first administrator, then remove the `CF_BOOTSTRAP__*` lines and run `docker compose up -d` again. Add passkeys or an authenticator app under *Account*.

### Data

Everything the aggregator keeps is in the `aggregator-data` volume (`/data`): the SQLite database, the Data Protection key ring, and git clones. **The key ring is the only way to decrypt stored credentials, tokens and the servers' private age keys**, and through them, every secret in your repositories encrypted only to those keys. Back it up together with the database (see [Backups](#5-backups)). Caddy's certificates are in `caddy-data`.

The aggregator's image includes `sops`, `age`, `age-keygen` and `ssh-keygen`; nothing else needs installing on the host.

## 2. Servers (agents)

In the panel, open *Environments → New environment*. It shows a one-time enrollment token and a ready compose file for the agent, the same as [deploy/agent/compose.yaml](../deploy/agent/compose.yaml) with your domain filled in. On the server:

```sh
mkdir -p /srv/container-forest-agent && cd /srv/container-forest-agent
# save the compose file from the panel as compose.yaml, then:
cat > .env <<'EOF'
CF_DOMAIN=forest.example.com
ENROLLMENT_TOKEN=<token from the panel>
AGENT_HOSTNAME=web-1
EOF
chmod 600 .env
docker compose up -d
```

The server shows up online in the panel within seconds. The token is needed only on the first start: the agent stores its own credentials in `/opt/container-forest`. To enroll it again, issue a new token in the panel and delete `/opt/container-forest/agent-credentials.json`.

Things to know about the agent's compose file:

- **Directories are mounted at the same path** on the host and in the container (`/opt/container-forest`, `/run/container-forest`): `docker compose` inside the agent talks to the host's daemon, which resolves bind mounts on the host.
- **It mounts the Docker socket, so the agent is effectively root on the host.** That's what lets it run your stacks. The aggregator can only send it a fixed set of typed commands, never a shell command ([security invariants](security-invariants.md)).
- **The host root is mounted read-only** at `/host` so that compose policies can check bind-mount sources for symbolic links.
- **Unmanaged stacks.** To let the panel pull, start, restart and stop compose projects you run by hand (say, in `/srv/docker`), uncomment `CF_AGENT__UNMANAGEDSTACKROOTS__0` and the matching volume. See [design/unmanaged-stacks.md](design/unmanaged-stacks.md).
- **The agent can't be stopped from the panel**, only started: that would cut the server off. The same goes for the aggregator and panel containers.

## 3. Stacks

A stack is one compose project deployed from a git repository.

1. **Add the repository** under *Repositories*. Public repositories need only a URL. For private ones, an administrator first adds a **git connection**: a URL prefix (an organisation or a whole server), a read-only token, and the environments allowed to deploy from it. Suitable tokens: on GitHub a fine-grained token with `Contents: Read-only`; on Gitea a service user's token with `read:repository`; on GitLab a deploy token with `read_repository`. After that, editors add repositories under the prefix without seeing the token.
2. **Create the stack** on an environment: repository, branch and the path to its `compose.yaml`. The compose file must use ready images: `build:` is rejected.
3. **Deploy** it from the panel, `forest`, or Discord. Each deployment records its commit and keeps a log.
4. **Automate it** if you like: turn on *deploy on push* and add the webhook the panel shows to your git server (GitHub, Gitea/Forgejo or GitLab), or create a *deploy hook* and call it from CI after pushing an image.

The operations are separate on purpose: **restart** restarts the containers as they are, **redeploy** applies the current commit, **redeploy with pull** also pulls images, and **down** stops and removes the stack.

### Secrets

Secrets are committed encrypted with [SOPS](https://github.com/getsops/sops) to the server's age key, and the aggregator decrypts them just before a deployment. In the panel, create an age key for the environment (or for one stack) under the environment's *Keys*, then in your clone of the repository:

```sh
forest login https://forest.example.com --secrets
forest secrets init -e <environment>   # writes .sops.yaml and .gitattributes, sets up a git filter and a pre-commit hook
```

From then on you edit secret files in plain text in your clone and git stores them encrypted. You can also use `sops` directly with the public key from the panel. How it works and how to rotate keys: [design/secrets.md](design/secrets.md).

### Access

Roles are given on an environment or on a single stack; the effective role on a stack is the higher of the two.

- **Viewer** sees stacks, containers and logs. Logs can contain secrets that applications print themselves.
- **Operator** also starts, stops and restarts containers.
- **Editor** also edits and deploys stacks and adds repositories.
- **Environment admin** (on a whole environment) also sets its compose policy: which mounts, capabilities and networks its stacks may use ([design/compose-policy.md](design/compose-policy.md)).
- **Admin** manages everything, including users, git connections and settings.

**Without a compose policy, an Editor is effectively root on the host:** a compose file can mount `/` or run a privileged container. The same goes for anyone who can push to a stack's branch. Turn on the policy where that isn't acceptable, and consider requiring signed commits. Details: [architecture.md](architecture.md).

## 4. Discord bot (optional)

The bot is a stateless container with only outbound connections: [deploy/discord-bot/compose.yaml](../deploy/discord-bot/compose.yaml). It can run anywhere that reaches Discord and the aggregator.

1. Create a Discord application (https://discord.com/developers/applications), copy its bot token, and invite the bot to your server with the `bot` and `applications.commands` scopes.
2. Generate a service token for the bot and give its hash to the aggregator:

   ```sh
   BOT_SERVICE_TOKEN=$(openssl rand -hex 32)
   printf '%s' "$BOT_SERVICE_TOKEN" | sha256sum | cut -d' ' -f1   # → CF_BOTGATEWAY__TOKENHASHES__0 in the aggregator's .env
   ```

3. Put `DISCORD_TOKEN` and `BOT_SERVICE_TOKEN` in the bot's `.env` (readable by root only), set `CF_BOT__CONTROLLERURL` to `https://<CF_DOMAIN>`, and `docker compose up -d`.
4. Each person links their Discord account from the panel (*Account*) or with `/link <code>`, and then has the same permissions in Discord as in the panel.

**Both tokens are as powerful as an administrator:** whoever has the bot can act as any linked user. Rotation: [design/discord-bot.md](design/discord-bot.md). A wrong Discord token stops the container with exit code 1 and a hint in the log.

## 5. Backups

The aggregator can back itself up to any S3-compatible bucket (Cloudflare R2, AWS S3, MinIO, …) on a schedule. Archives are encrypted to an age key **whose private part never touches the aggregator**: keep it offline, in two places.

```sh
age-keygen -o forest-backup.key   # on a separate machine; the public key goes into the panel
```

Fill in *Settings → Backup*, press *Back up now*, check that the object appeared in the bucket, then turn on the schedule. Add a system-wide notification rule for `BackupFailed`. The step-by-step setup for R2, the restore procedure and what to do after a restore: [design/backup.md](design/backup.md).

If you'd rather snapshot the whole disk, the snapshot must be atomic (ZFS, LVM, your provider's snapshots) and include both the database and the key ring.

## 6. Upgrades

All components share one version, and an older agent or bot keeps working with a newer aggregator, so you can upgrade them separately and in any order, aggregator first being the usual one.

```sh
docker compose pull && docker compose up -d
```

- `:latest` is always the last release. To upgrade only when you choose, pin `:X.Y.Z` instead.
- Read the [changelog](../CHANGELOG.md) first: it says which components changed, and before 1.0 a minor release may contain breaking changes.
- The aggregator migrates its database on start and keeps `aggregator.db.before-migration` in its volume until the next migration.
- The CLI: download the new binary from the [releases](https://github.com/lepterion/container-forest/releases).

## Troubleshooting

- **A server stays offline.** Check the agent's log (`docker compose logs`). It must reach `https://<CF_DOMAIN>` over HTTP/2. If enrollment fails, the token may have been used, expired or replaced: issue a new one.
- **"Docker is not reachable on this host".** The agent can't reach `/var/run/docker.sock`; check the mount and that Docker is running.
- **A deployment fails at the git step.** For a private repository, check the git connection's prefix and that its token can read the repository. Public repositories on private network addresses are refused unless they go through a connection.
- **A deployment fails decrypting secrets.** The file is encrypted to a key this environment doesn't have: check `.sops.yaml` against the public keys in the panel.
- **Everything stored is suddenly unreadable** after moving or restoring the aggregator: the Data Protection key ring wasn't moved with the database.
