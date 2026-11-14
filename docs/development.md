# Development guide

How to run every part of container-forest locally, exercise its features, and what CI and releases do. Contribution rules are in [CONTRIBUTING.md](../CONTRIBUTING.md).

You need the .NET 10 SDK, Node.js 22.12+ (CI and the image use 24, see `src/Web/.nvmrc`) and Docker (Docker Desktop on Windows). Without a local .NET SDK you can build and test in a `mcr.microsoft.com/dotnet/sdk:10.0` container with the repository mounted at `/src`.

## Naming

| Where | How |
|---|---|
| Docker images | `container-forest-aggregator`, `container-forest-agent`, `container-forest-web`, `container-forest-discord-bot` |
| .NET projects and namespaces | `ContainerForest.*` |
| Protobuf packages | `<module>` with no prefix or version; the file is `src/Contracts/<module>/` (ADR-0002) |
| Environment variables | prefix `CF_`, nested sections separated by `__` (e.g. `CF_AGENT__CONTROLLERURL`) |
| Directories on hosts | `/opt/container-forest` (stacks, agent data), `/run/container-forest` (secrets, tmpfs) |

## Aggregator

```sh
CF_BOOTSTRAP__ADMINUSERNAME=root CF_BOOTSTRAP__ADMINPASSWORD='<password>' dotnet run --project src/Aggregator/Api
```

REST listens on `http://localhost:5080`, gRPC for agents and the bot on `http://localhost:5081`. The SQLite database (`src/Aggregator/Api/aggregator.db`) is created and migrated on start. The first administrator is created from `CF_BOOTSTRAP__ADMINUSERNAME` and `CF_BOOTSTRAP__ADMINPASSWORD` (at least 12 characters) when the database has no users; there is no other way to create one.

```sh
curl -c cookies.txt -X POST http://localhost:5080/api/auth/login -H 'content-type: application/json' -d '{"userName":"root","password":"<password>","rememberMe":false}'
curl -b cookies.txt http://localhost:5080/api/me
```

In the `Development` environment the `X-Dev-User` header signs a request in as the named user without a password. The examples below use it with the administrator `root`.

### Environments and containers

Creating an environment returns an enrollment token for its agent. The panel does the same under *Environments → New environment* and shows a ready compose file for the agent.

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments -H 'content-type: application/json' -d '{"name":"local","isPrivate":false}'   # enrollmentToken is shown only once
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/enrollment-token   # a new token; the previous one stops working
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/revoke-agent       # forget the agent and drop its connection
curl -H 'X-Dev-User: root' http://localhost:5080/api/environments                   # visible environments and agent status
curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/containers   # visible containers, through the agent
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/containers/<container>/restart   # start | stop | restart, ?timeoutSeconds=30; <container> is a full id, a name or a unique id prefix, as in docker
curl -H 'X-Dev-User: root' 'http://localhost:5080/api/environments/<id>/containers/<container>/logs?tail=100'   # last lines; ?since=<ISO 8601> for newer ones only
```

### Users and roles

Administrators only; the full API is in `src/Aggregator/Api/Contracts/UserContracts.cs`.

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/users -H 'content-type: application/json' -d '{"userName":"alice","password":"<12+ characters>","isAdmin":false}'
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/users/<userId>/role-assignments -H 'content-type: application/json' -d '{"environmentId":"<id>","stackId":null,"role":"Operator"}'   # with stackId, the role applies to that stack only
curl -H 'X-Dev-User: root' http://localhost:5080/api/users/<userId>   # the user and their roles
```

### API tokens

ADR-0007. Only a session (cookie or `X-Dev-User`) creates a token, and it's shown once. `readOnly`, `environmentIds` (null for all) and `expiresAt` (null for none) restrict it.

```sh
TOKEN=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/tokens -H 'content-type: application/json' -d '{"name":"laptop","readOnly":false,"environmentIds":null,"expiresAt":null}' | jq -r .token)
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/me
curl -X DELETE -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/me/tokens/current   # revoke the token itself
```

### Deploying a stack

The compose file must use ready images (`build:` is rejected). `https://github.com/dockersamples/example-voting-app.git` with `composePath` set to `docker-compose.images.yml` works for a test.

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/repositories -H 'content-type: application/json' -d '{"name":"infra","url":"https://github.com/<org>/<repo>.git","connectionId":null}'
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/stacks -H 'content-type: application/json' -d '{"name":"web","repositoryId":"<repoId>","branch":"main","composePath":"apps/web/compose.yaml"}'
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deployments -H 'content-type: application/json' -d '{"pull":false}'   # pull=true for redeploy with pull
curl -H 'X-Dev-User: root' http://localhost:5080/api/deployments/<deploymentId>   # status and log
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/restart?timeoutSeconds=10   # restart | down; the response carries compose's output
```

Private repositories go through a git connection (ADR-0006, administrators only): a URL prefix, a read-only token and the environments allowed to deploy from it. Suitable tokens: on Gitea, a service user's token with the `read:repository` scope; on GitHub, a fine-grained token with `Contents: Read-only`; on GitLab, a deploy token with `read_repository` (then `userName` is the token's name). The token is write-only: responses only say `hasAccessToken`. Anyone with the Editor role in an allowed environment can add repositories under the connection, without a token:

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/git-connections -H 'content-type: application/json' -d '{"name":"team","urlPrefix":"https://gitea.example.com/team/","accessToken":{"userName":null,"token":"<token>"},"allEnvironments":false,"environmentIds":["<id>"]}'
curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/git-connections/<connectionId>/access-token -H 'content-type: application/json' -d '{"userName":null,"token":"<new token>"}'
curl -X POST -H 'X-Dev-User: alice' http://localhost:5080/api/repositories -H 'content-type: application/json' -d '{"name":"app","url":"https://gitea.example.com/team/app.git","connectionId":"<connectionId>"}'
```

A public repository is never fetched from private network addresses (loopback, RFC 1918, link-local). For an internal git server, create a connection (a token is optional) or set `CF_GIT__ALLOWPRIVATEHOSTS=true`. Clones live in `git-cache/` in the working directory (`CF_GIT__CACHEDIRECTORY`).

### Deploy on push and deploy hooks

How they work: [architecture.md](architecture.md). The git server has to reach the aggregator, so locally it's easiest to send a signed webhook by hand:

```sh
SECRET=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/repositories/<repoId>/webhook | jq -r .secret)   # shown only once
curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deploy-on-push -H 'content-type: application/json' -d '{"enabled":true}'
BODY='{"ref":"refs/heads/main","after":"<commit sha>"}'
curl -X POST http://localhost:5080/api/webhooks/<repoId> -H 'content-type: application/json' -H 'X-GitHub-Event: push' \
  -H "X-Hub-Signature-256: sha256=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -r | cut -d' ' -f1)" -d "$BODY"
```

A deploy hook for CI: the token is shown once, the aggregator stores its hash.

```sh
HOOK=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deploy-hook | jq -r .token)
curl -X POST -H "Authorization: Bearer $HOOK" http://localhost:5080/api/deploy-hooks/<stackId>   # 202: a deployment with image pull is queued
```

### SOPS secrets

How they work: [design/secrets.md](design/secrets.md). The aggregator needs `sops` and `age-keygen` on `PATH`; without them, deploying stacks with encrypted files and creating keys fail, and everything else works. Install the same versions as the image with `scripts/install-sops.sh ~/.local/bin`. Environment keys (administrators only; responses carry only the public key):

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/age-keys
curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/age-keys
sops encrypt --age <recipient>,<your key> --in-place .env   # or a rule in .sops.yaml
```

The git filter (ADR-0010) in a test repository: the CLI must be on `PATH` as `forest` (git runs it itself), with a token allowed to decrypt secrets:

```sh
forest login --secrets http://localhost:5173     # keep "Decrypt secrets" checked in the browser
forest secrets init -e local                      # .sops.yaml, .gitattributes, the filter and a pre-commit hook
printf 'TOKEN=s3cret\n' > .env && git add .env && git show :.env   # git stores the encrypted file
```

Without a published binary, a `forest` wrapper on `PATH` will do: `exec dotnet <repo>/src/Cli/bin/Debug/net10.0/forest.dll "$@"`.

### Signed commits

ADR-0011. The aggregator needs `ssh-keygen` (package `openssh-client`) on `PATH`. A person adds their own signing key (a session, not an API token), a stack turns the requirement on, and an unsigned commit can be approved by SHA:

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/signing-keys -H 'content-type: application/json' -d "{\"name\":\"laptop\",\"publicKey\":\"$(cat ~/.ssh/id_ed25519.pub)\",\"environmentIds\":null}"
curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/require-signed-commits -H 'content-type: application/json' -d '{"enabled":true}'
git config gpg.format ssh && git config user.signingkey ~/.ssh/id_ed25519.pub && git commit -S -m '…'
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deployments -H 'content-type: application/json' -d '{"pull":false,"approveCommit":"<full SHA>"}'   # only for an unsigned commit
```

### Compose policy and environment administrators

ADR-0012. An administrator assigns the `EnvironmentAdmin` role on an environment; that person changes the policy and its allowances. Rendering checks need `docker compose` on the agent; paths outside `/opt/container-forest` need the host root (`CF_AGENT__HOSTROOT`, `/:/host:ro,rslave` in the agent's container):

```sh
curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/role-assignments -H 'content-type: application/json' -d '{"userName":"alice","stackId":null,"role":"EnvironmentAdmin"}'
curl -X PUT -H 'X-Dev-User: alice' http://localhost:5080/api/environments/<id>/compose-policy -H 'content-type: application/json' -d '{"mode":"Audit","dataRoot":"/var/lib/container-forest/data","systemReadOnlyPaths":["/etc/localtime"],"allowances":["network:proxy"]}'
curl -X PUT -H 'X-Dev-User: alice' http://localhost:5080/api/stacks/<stackId>/policy-allowances -H 'content-type: application/json' -d '{"allowances":["docker-socket"]}'
curl -H 'X-Dev-User: alice' http://localhost:5080/api/environments/<id>/compose-policy/report   # deployed stacks against the current policy
curl -X POST -H 'X-Dev-User: alice' 'http://localhost:5080/api/environments/<id>/age-keys?stackId=<stackId>'   # a SOPS key for this stack only
```

### Private registries

Credentials are set per environment (administrators only; the password is never returned). Another `PUT` for the same `server` replaces the login:

```sh
curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials -H 'content-type: application/json' -d '{"server":"ghcr.io","username":"<user>","password":"<token>"}'
curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials
curl -X DELETE -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials/<credentialId>
```

Passwords are encrypted with the keys in `data-protection-keys/` in the aggregator's working directory (`CF_DATAPROTECTION__KEYDIRECTORY`): without that directory, stored credentials can't be decrypted.

## Agent

```sh
CF_AGENT__ENROLLMENTTOKEN=<token> dotnet run --project src/Agent
```

```powershell
$env:CF_AGENT__ENROLLMENTTOKEN = "<token>"
dotnet run --project src/Agent
```

The agent enrolls, connects to the aggregator and to the local Docker. In `Development` the aggregator's address is already `http://localhost:5081` (otherwise set `CF_AGENT__CONTROLLERURL`). Credentials are stored in `agent-credentials.json` in the data directory (`CF_AGENT__DATADIRECTORY`; `/opt/container-forest` by default, `%LOCALAPPDATA%\container-forest` on Windows), so the token is needed only on the first start. Delete that file to enroll again.

The agent's image is built from the repository root (the build needs `src/Contracts` and `Directory.*.props`) and runs like in [deploy/agent/compose.yaml](../deploy/agent/compose.yaml). To connect it to an aggregator started with `dotnet run`, use `http://host.docker.internal:5081`:

```sh
docker build -f src/Agent/Dockerfile -t container-forest-agent:dev .
docker run -d --name cf-agent --hostname my-laptop \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v cf-agent-data:/opt/container-forest \
  -e CF_AGENT__CONTROLLERURL=http://host.docker.internal:5081 \
  -e CF_AGENT__ENROLLMENTTOKEN=<token> \
  container-forest-agent:dev
```

## Discord bot

The bot connects to the same gRPC port `5081`. The aggregator accepts it only when its configuration holds the hash of the bot's service token:

```sh
TOKEN=$(openssl rand -hex 32)                                              # for the bot: CF_BOT__SERVICETOKEN
export CF_BOTGATEWAY__TOKENHASHES__0=$(printf '%s' "$TOKEN" | sha256sum | cut -d' ' -f1)   # for the aggregator, before dotnet run
dotnet user-secrets set Bot:ServiceToken "$TOKEN" --project src/DiscordBot
```

In PowerShell (there is no `sha256sum`):

```powershell
$bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$env:CF_BOTGATEWAY__TOKENHASHES__0 = -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') })   # for the aggregator, before dotnet run
dotnet user-secrets set Bot:ServiceToken $token --project src/DiscordBot
```

The service token is as powerful as an administrator's access; details and rotation are in [design/discord-bot.md](design/discord-bot.md).

You also need your own Discord test application (https://discord.com/developers/applications → Bot → Reset Token) and a test server the bot is invited to with the `bot` and `applications.commands` scopes (OAuth2 → URL Generator). To get the server's id, turn on Developer Mode in Discord and right-click the server → Copy Server ID. In `Development`, slash commands are registered on that server only, where they appear immediately:

```sh
dotnet user-secrets set Discord:Token "<Discord bot token>" --project src/DiscordBot
dotnet user-secrets set Discord:DevGuildId "<server id>" --project src/DiscordBot
dotnet run --project src/DiscordBot
```

In `Development` the aggregator's address is already `http://localhost:5081` (otherwise `CF_BOT__CONTROLLERURL`). To use the commands, link your Discord account: get a code (`curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/discord/link-code`) and run `/link <code>` in Discord. Only the author of a command sees the bot's replies.

The bot's image is built the same way; a deployment example is [deploy/discord-bot/compose.yaml](../deploy/discord-bot/compose.yaml). Locally, pass the tokens in an `.env.local` file with `CF_DISCORD__TOKEN=…` and `CF_BOT__SERVICETOKEN=…` (it's in `.gitignore`) and point `CF_BOT__CONTROLLERURL` at `http://host.docker.internal:5081`. The image doesn't read user-secrets and starts in `Production`, so commands are registered globally; for a test server add `-e DOTNET_ENVIRONMENT=Development -e CF_DISCORD__DEVGUILDID=<server id>`:

```sh
docker build -f src/DiscordBot/Dockerfile -t container-forest-discord-bot:dev .
docker run -d --name cf-discord-bot --env-file .env.local \
  -e CF_BOT__CONTROLLERURL=http://host.docker.internal:5081 \
  container-forest-discord-bot:dev
```

## Web panel

```sh
cd src/Web && npm ci
CF_DEV_USER=root npm run dev        # PowerShell: $env:CF_DEV_USER="root"; npm run dev
```

The panel runs on `http://localhost:5173`. The dev server proxies `/api` and `/hubs` to the aggregator (`CF_AGGREGATOR_URL`, `http://localhost:5080` by default) and, when `CF_DEV_USER` is set, adds `X-Dev-User` with its value to every request. Without it you land on `/login`, where you can sign in with a password, for example as the `CF_BOOTSTRAP__*` administrator.

- Changed a DTO in the aggregator: build `src/Aggregator/Api` (which updates the OpenAPI document) and run `npm run generate:api`; the client types in `src/api/schema.gen.ts` are generated from it.
- The PWA (service worker, installing, offline) exists only in a production build: `npm run build`, then `CF_DEV_USER=root npm run preview` on `http://localhost:4173` with the same proxy. After changing the logo, `scripts/pwa-icons.sh` re-renders the icons (needs Google Chrome).
- `src/routeTree.gen.ts` is generated by the TanStack Router plugin during `npm run dev`/`build`. Both generated files are committed and never edited by hand.
- User-visible text never goes into markup: add a key to `src/i18n/locales/` and use `const { t } = useTranslation()` and `t("screen.key")` in the component. A missing translation or an unknown key fails `npm run typecheck`.
- UI components are shadcn/ui: `npm run ui:add -- table` (from `src/Web`) adds a component to `src/components/ui` and switches its icons to Material Symbols; the generated files are committed. If `npm run typecheck` then complains about a missing icon, add it to `src/components/icons.ts` (names as in Lucide, icons from https://fonts.google.com/icons).
- The panel's Docker image (Caddy: static files, proxying `/api`, `/hubs` and gRPC to the aggregator, TLS, ADR-0005): `docker build -f src/Web/Dockerfile -t container-forest-web:dev .` (the context is the repository root: the panel embeds `deploy/agent/compose.yaml`).

Sign-in and account linking with Discord (ADR-0009) need a Discord test application with the redirect `http://localhost:5080/api/auth/discord/callback` and `CF_DISCORDAUTH__CLIENTID` / `CF_DISCORDAUTH__CLIENTSECRET` on the aggregator. The Vite proxy rewrites `Host`, so Discord sends the browser straight back to the aggregator (`localhost:5080`) rather than the panel: after signing in, open `http://localhost:5173` yourself.

## CLI

ADR-0008.

```sh
dotnet run --project src/Cli -- login http://localhost:5173
```

The address is the panel's (the Vite dev server): the CLI opens its confirmation page in the browser, and Vite proxies the API to the aggregator. The CLI writes its configuration and token to `$XDG_CONFIG_HOME/container-forest/config.json`; set `XDG_CONFIG_HOME` for a separate test configuration. Without a browser: `--device` (the code is entered at `/cli/device`), or a token from the *API tokens* screen: `CF_URL=http://localhost:5173 CF_TOKEN=cf_pat_… dotnet run --project src/Cli -- env list`.

- Changed the aggregator's API: after building `src/Aggregator/Api`, regenerate the client with `dotnet tool restore && scripts/generate-cli-client.sh` and commit the generated files (CI checks them).
- A single binary with no runtime (NativeAOT, needs clang): `dotnet publish src/Cli -r linux-x64 -c Release -o out` → `out/forest`.

## Running the whole system in Docker

`deploy/aggregator/compose.yaml` can be tried locally without a domain or TLS: build the aggregator and panel images, set `CF_DOMAIN=:8080` and the first administrator in `.env`, and in a `compose.override.yaml` use the local images and publish `8080:8080` instead of 80 and 443. Agents then connect to `http://<host>:8080`: Caddy passes gRPC without TLS too (h2c).

```sh
docker build -f src/Aggregator/Api/Dockerfile -t container-forest-aggregator:dev .
docker build -f src/Web/Dockerfile -t container-forest-web:dev .
```

## Tests

```sh
dotnet test --solution ContainerForest.slnx
```

Tests with real `sops` and `age-keygen` are skipped when those aren't on `PATH` (required in CI: `CF_TEST_REQUIRE_SOPS=1`), and so are signature tests with real `ssh-keygen` (`CF_TEST_REQUIRE_SSH_KEYGEN=1`) and agent rendering tests with real `docker compose` (`CF_TEST_REQUIRE_DOCKER_COMPOSE=1`; they don't need a Docker daemon). Integration tests in `tests/Aggregator/Api` start the aggregator in memory and connect to it as an agent. Tests in `tests/DiscordBot` start an in-memory `BotGateway` gRPC server with canned answers.

## EF Core migrations

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Aggregator/Infrastructure --output-dir Persistence/Migrations
```

## Documentation site

DocFX builds the documents in `docs/` and the API reference from the XML comments in `src/`. Generated code (protobuf, migrations) is left out of the reference (`docs/api-filter.yml`). The `Docs` workflow builds the site on every PR and publishes it to GitHub Pages from `main`. Locally:

```sh
dotnet tool restore
dotnet docfx docfx.json --serve   # http://localhost:8080
```

## CI

GitHub Actions (`.github/workflows/ci.yml`) runs on every PR and every push to `main`:

1. `dotnet restore` and `dotnet build` with `CI=true`: compiler and analyzer warnings become errors (`Directory.Build.props`).
2. The committed `src/Aggregator/Api/openapi/*.json` must match what the build generated. If this step fails, build `src/Aggregator/Api` locally and commit the JSON. The same goes for the CLI client: `scripts/generate-cli-client.sh` must not change anything in `src/Cli/Api/Generated`.
3. `dotnet test --solution ContainerForest.slnx`.
4. A separate job for the panel (`src/Web`): `npm ci`, a check that `src/api/schema.gen.ts` matches the OpenAPI document, `npm run build` (type checks and the build), and a check that `src/routeTree.gen.ts` is committed up to date.

The documentation workflow (`.github/workflows/docs.yml`) builds the site and runs `scripts/check-docs`:

- every Mermaid diagram in Markdown files parses;
- names and file paths in backticks in `CLAUDE.md`, `CONTRIBUTING.md`, `src/*/CLAUDE.md`, `docs/design/`, `docs/architecture.md`, `docs/development.md` and `docs/project-status.md` exist in the code. If the step fails, fix the document; add only a truly external name to `scripts/check-docs/identifier-allowlist.txt`;
- the root `CLAUDE.md` is at most 150 lines.

Run the docs checks locally with `cd scripts/check-docs && npm ci && npm run check`. The commands to reproduce the main CI job are in [CONTRIBUTING.md](../CONTRIBUTING.md#commits-and-pull-requests).

## Releases

All components (aggregator, panel, agent, bot and CLI) share one version `X.Y.Z` following [semver](https://semver.org/), so the number tells you what works with what: agent 0.9.x works with aggregator 0.9.x. The protocol changes only compatibly, so an older agent or bot also works with a newer aggregator.

- **release-please computes the version** (the `googleapis/release-please-action` GitHub Action, workflow `Release`) from the Conventional Commits on `main`. While the version is 0.x, `feat` bumps the minor version, `fix` and `perf` the patch version, and a breaking change (`feat!:` or `BREAKING CHANGE:` in the body) also bumps the minor version. `docs`, `ci`, `chore`, `test` and `refactor` don't change the version and don't appear in the changelog.
- **The release PR.** After every push to `main`, release-please opens or updates the PR `chore(main): release X.Y.Z` with `CHANGELOG.md`, `version.txt` and `<Version>` in `Directory.Build.props`. Merging it releases: the tag `vX.Y.Z` and a GitHub Release are created, and the same workflow publishes the images and attaches the CLI binaries (linux x64/arm64, macOS arm64, Windows x64, with `SHA256SUMS`). The PR is created with `GITHUB_TOKEN`, so CI doesn't run on it; it only changes the version and the changelog.
- **Image tags.** `ghcr.io/lepterion/container-forest-<name>:X.Y.Z`, `:X.Y` and `:latest` come only from releases, so `latest` is always the last release. Every push to `main` publishes `:main` and `:<sha>` for testing before a release.
- **Upgrading.** You don't have to upgrade everything on every release: the changelog shows which components changed (`feat(web)`, `fix(agent)`), and those are the ones worth upgrading. To stay on a release, pin `:X.Y.Z` in compose instead of `:latest`.
- **Builds outside a release** carry the last release's version from `Directory.Build.props`; the exact commit is in `InformationalVersion` (`0.8.0+<sha>`).
- **History.** Before 0.8.0 only the CLI had a version (tags `cli-v0.1.0`…`cli-v0.7.0`); the shared numbering continues it.
