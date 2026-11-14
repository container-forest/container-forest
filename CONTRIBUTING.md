# Contributing to container-forest

Thanks for your interest! Bug reports, ideas, documentation fixes and code are all welcome, from anyone.

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to help

- **Report a bug.** Open an issue with what you did, what you expected, what happened, the version (`forest --version`, or the image tag) and relevant logs. Logs can contain secrets: check before pasting.
- **Suggest a feature.** Open an issue describing the problem first, not only the solution. container-forest deliberately stays small (plain Docker and Compose, see the README), so some ideas will get a "no, but…".
- **Fix something.** Issues labelled [`good first issue`](https://github.com/lepterion/container-forest/labels/good%20first%20issue) are a good start. For anything bigger than a small fix, comment on the issue or open one before writing a lot of code, so we can agree on the approach.
- **Improve the docs.** Typos, unclear steps and missing explanations are worth a PR on their own.

Security problems: don't open a public issue, follow [SECURITY.md](SECURITY.md).

## Ground rules

These apply to every change, whoever writes it:

- **Security invariants.** [docs/security-invariants.md](docs/security-invariants.md) lists what no PR may break: private keys stay on the aggregator, agents open no ports, permissions are checked only on the aggregator, and so on. If your change needs to bend one, open an issue first.
- **Compatibility with running installations.** Agents, the bot and the aggregator are upgraded separately, and people run container-forest in production.
  - `.proto` contracts change only compatibly: new fields and new `oneof` cases, never renumbered or removed fields (a removed field goes to `reserved`). See ADR-0002.
  - EF Core migrations must not lose data.
  - A new required setting needs a default.
- **Scope.** Discuss new dependencies and new subsystems in an issue first. For a dependency, say why it's needed and check its license (no GPL or AGPL in code we link against).

## Development setup

You need the .NET 10 SDK, Node.js 22.12+ (CI uses 24, see `src/Web/.nvmrc`) and Docker. On Windows, Docker Desktop.

A minimal local loop:

```sh
# 1. Aggregator: REST on :5080, gRPC for agents and the bot on :5081. SQLite is created and migrated on start.
CF_BOOTSTRAP__ADMINUSERNAME=root CF_BOOTSTRAP__ADMINPASSWORD='local-dev-password' \
  dotnet run --project src/Aggregator/Api

# 2. Panel on http://localhost:5173, proxying /api and /hubs to the aggregator.
#    CF_DEV_USER signs every request in as that user (Development only).
cd src/Web && npm ci && CF_DEV_USER=root npm run dev

# 3. Agent: create an environment in the panel, copy its enrollment token.
CF_AGENT__ENROLLMENTTOKEN=<token> dotnet run --project src/Agent
```

[docs/development.md](docs/development.md) covers the rest: the API with `curl`, the Discord bot, the CLI, SOPS and the git filter, signed commits, compose policies, Docker images, the docs site and EF Core migrations.

### Tests

```sh
dotnet test --solution ContainerForest.slnx
```

Tests that need real `sops`/`age-keygen`, `ssh-keygen` or `docker compose` are skipped when those aren't on `PATH` (`scripts/install-sops.sh ~/.local/bin` installs the same SOPS versions as CI). In CI they are required. A failing test is a bug to fix: don't disable or skip tests to get a green build, always resolve CI issues.

## Repository layout

```
src/
  Contracts/        .proto files; each consumer generates its own C# code
  Aggregator/       central service: Domain ← Application ← Infrastructure ← Api
  Agent/            .NET worker that runs on every server
  DiscordBot/
  Cli/              the forest CLI (NativeAOT); Api/Generated is the Kiota client
  Web/              the panel: Vite + React + TypeScript
tests/              mirrors src/
deploy/             compose files for the aggregator with the panel, the agent and the bot
docs/               architecture, design notes, ADRs, guides
scripts/            SOPS install, client generation, docs checks
```

Why it's split this way: [ADR-0001](docs/adr/0001-solution-structure.md). Before changing a subsystem, read its note in [docs/design/](docs/design/README.md).

## Conventions

- **Code style.** C# follows the [.NET conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) with the rules in `.editorconfig`. In CI, compiler and analyzer warnings are errors.
- **Comments and docs** are in English. Write `///` comments for public types and members: the API reference is generated from them.
- **Configuration** goes through options classes bound from environment variables with the `CF_` prefix, nested sections separated by `__` (`CF_AGENT__CONTROLLERURL`), validated on start.
- **Generated files are committed and never edited by hand:** the OpenAPI documents in `src/Aggregator/Api/openapi/`, the panel's `src/api/schema.gen.ts` and `src/routeTree.gen.ts`, the CLI client in `src/Cli/Api/Generated`. CI checks they are up to date.
- **Panel text** is never hard-coded: add a key to `src/Web/src/i18n/locales/en.ts` and its translation to `uk.ts`. A missing key fails `npm run typecheck`. If you don't speak Ukrainian, leave the English text in `uk.ts` and say so in the PR; a maintainer will translate it.
- **Documentation changes go in the same PR as the behaviour change:** the subsystem's note in `docs/design/`, or the component's `CLAUDE.md` for conventions. A decision that changes something in [docs/design/decisions.md](docs/design/decisions.md) gets a short ADR in `docs/adr/`.

## Commits and pull requests

- **Branch from `main`, open the PR against `main`.** No direct pushes to `main`.
- **Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)** with the component as scope: `feat(agent): …`, `fix(web): …`, `docs: …`. Releases and the changelog are generated from them, so the type matters: `feat` and `fix` show up in the changelog, a breaking change needs `!` or a `BREAKING CHANGE:` footer.
- **Keep a PR to one change.** Small PRs get reviewed faster.
- **CI must be green.** To reproduce it locally before pushing:

  ```sh
  CI=true dotnet build ContainerForest.slnx      # PowerShell: $env:CI="true"; dotnet build ContainerForest.slnx
  git status src/Aggregator/Api/openapi           # must show no changes
  dotnet test --solution ContainerForest.slnx --no-build
  cd src/Web && npm run generate:api && npm run build && git status .   # must show no changes
  ```

  Docs checks (Mermaid diagrams parse, file and type names in backticks exist in the code): `cd scripts/check-docs && npm ci && npm run check`.
- **A maintainer reviews every PR.**

Signed commits are welcome but not required.

## AI-assisted development

You may use AI coding tools. You are still the author: understand every line you submit and be ready to explain it, and make sure it passes the same checks as hand-written code. The repository has `CLAUDE.md` files with project rules for Claude Code; other assistants can read them too.

## Releases

All components share one version, `X.Y.Z`, so the number tells you what works with what. [release-please](https://github.com/googleapis/release-please) keeps a release PR open with the next version and changelog; merging it tags `vX.Y.Z`, publishes the images (`:X.Y.Z`, `:X.Y`, `:latest`) and attaches the CLI binaries. Every push to `main` also publishes images tagged `:main`. `CHANGELOG.md` and `version.txt` are written by release-please only.

## License

container-forest is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution you agree that it is licensed under the same terms (section 5 of the license). No CLA, no sign-off. Don't submit code you don't have the right to share.
