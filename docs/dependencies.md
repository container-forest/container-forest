# Key dependencies

The third-party libraries container-forest is built on: what each one does here, why it was picked over the alternatives, and the limits you should know about before touching the code that uses it. Exact versions are in `Directory.Packages.props` and `src/Web/package.json`. The pitfalls we ran into live next to the code they affect: in the component's `CLAUDE.md` (`src/<component>/CLAUDE.md`) and in the subsystem notes in [docs/design/](design/README.md).

Adding a dependency: open an issue or say so in the PR, explain why it's needed and check its license (no GPL or AGPL in code we link against).

## Aggregator

| Library | Used for | Why this one | Limits |
|---|---|---|---|
| [EF Core](https://github.com/dotnet/efcore) + SQLite | the whole domain model, migrations | built-in migrations; domain entities stored as they are (private setters, value converters); typed LINQ instead of SQL strings. Dapper, linq2db and NHibernate were considered. | SQLite has no `DateTimeOffset`: dates are stored as `long` by convention. Moving to another database would need its own migrations. |
| [ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity) | accounts: password hashing, lockout, security stamps, passkeys (WebAuthn) | proven password storage without writing crypto; built-in passkeys in .NET 10 mean no extra WebAuthn library. Keycloak/Authentik would be another service to deploy; OpenIddict and `MapIdentityApi` solve problems we don't have. | Defaults assume MVC (redirects, 30-minute stamp checks); several `UserManager` methods have side effects not visible from their names. See [design/auth.md](design/auth.md). |
| [AspNet.Security.OAuth.Discord](https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers) | sign-in and account linking with Discord | only fills in Discord's endpoints and claim mapping on top of ASP.NET Core's `OAuthHandler`, which keeps state, PKCE and correlation. | Its cookie defaults don't fit a `SameSite=Strict` session; the last step of the flow is ours. |
| [gRPC for .NET](https://github.com/grpc/grpc-dotnet) | agent and bot connections | a strict `.proto` contract with generated code on both sides, streaming built in, and a bidirectional stream the agent opens itself, so hosts need no inbound ports. SignalR and REST + WebSocket were considered. | No request/response semantics inside a stream (we correlate by `request_id`); no concurrent writes to one stream; HTTP/2 only, so gRPC has its own port. |
| [SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction) | live updates in the panel | part of ASP.NET Core: groups, the same cookie auth, transport fallback and reconnects. Raw WebSocket or SSE would need all of that written by hand. | Events are lost while disconnected; the panel refetches after reconnecting. |
| [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) | fetching stack repositories, reading commit trees, verifying commit signatures | no `git` binary or output parsing; files are read straight from the commit tree with no working copy; typed errors. | HTTPS only (the NuGet build has no SSH); synchronous API; cryptographic signature checks are done by `ssh-keygen`. |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | reading compose files for policy and image checks | the de facto YAML library for .NET, with anchors and merge keys, which compose files use a lot. | We walk the node tree, not a typed model: compose has too many shapes for one field. Error messages lack positions (we add them). |
| [AWSSDK.S3](https://github.com/aws/aws-sdk-net) | backups to S3-compatible storage | the common S3 implementation; avoids maintaining our own SigV4 signing. | ~2 MB of assemblies for one operation; defaults assume AWS (streaming signing and checksums are turned off for compatible servers). See [design/backup.md](design/backup.md). |
| [SOPS](https://github.com/getsops/sops) and [age](https://github.com/FiloSottile/age) (binaries) | encrypting and decrypting secrets in git | the established format for encrypted config in git; age keys are small and easy to rotate. | Must be on `PATH`; `scripts/install-sops.sh` pins versions and checksums. |

## Agent

| Library | Used for | Why this one | Limits |
|---|---|---|---|
| [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet) | containers, logs and events through the Docker Engine API | typed models and exceptions instead of parsing `docker` output. The fork is maintained by Testcontainers with regular releases; the original package is rarely updated. | `docker compose` still goes through the CLI. Its client API differs from the original package's examples. See `src/Agent/CLAUDE.md`. |

## Discord bot

| Library | Used for | Why this one | Limits |
|---|---|---|---|
| [Discord.Net](https://github.com/discord-net/Discord.Net) | slash commands, menus, notifications | the most widely used .NET Discord library; the Interaction Framework describes commands with attributes in DI-friendly modules. DSharpPlus, NetCord and Remora.Discord have smaller communities. | Its types are hard to construct in tests (we wrap them); Components V2 are barely covered by its guides; a wrong token only shows up as endless reconnects. |

## CLI

| Library | Used for | Why this one | Limits |
|---|---|---|---|
| [System.CommandLine](https://github.com/dotnet/command-line-api) | command tree and parsing | stable, NativeAOT-compatible, no reflection. Spectre.Console.Cli needs extra annotations for AOT; CommandLineParser has no nested commands. | Many online examples target pre-2.0 APIs: check against the package's XML docs. |
| [Kiota](https://github.com/microsoft/kiota) | the API client, generated from the aggregator's OpenAPI document | full OpenAPI 3.1 and NativeAOT support. NSwag targets 3.0; a hand-written client would drift from the server unnoticed. | Every model property is nullable and mutable (we wrap the client in an adapter); its default retry handler is removed. |

## Web panel

| Library | Used for | Why this one | Limits |
|---|---|---|---|
| [TanStack Query](https://tanstack.com/query) | server state: caching, retries, invalidation | no Redux; route loaders fill the same cache components read from. SWR and RTK Query were considered. | `clear()` also drops the mutation cache; use `removeQueries()` on sign-out. |
| [TanStack Router](https://tanstack.com/router) | routing | type-checked paths, params and search params, search params validated with Zod, loaders integrated with Query. React Router was considered. | `routeTree.gen.ts` is generated and committed. `navigate({ to })` only takes known routes; use `href` for arbitrary paths. |
| [React Hook Form](https://react-hook-form.com/) + [Zod](https://zod.dev/) | forms and validation | uncontrolled inputs (no re-render per keystroke), a ready Zod resolver, works with shadcn/ui. The same Zod schemas validate search params. Formik, TanStack Form, Yup and Valibot were considered. | Radix checkboxes aren't `<input>`s: wire them through `Controller`. |
| [i18next](https://www.i18next.com/) | translations | typed keys without code generation, a language detector out of the box. FormatJS's ICU messages and Lingui's build plugin are more than short UI labels need. | Schemas hold translation keys, not text, so messages follow the language switch. |
| [openapi-fetch](https://openapi-ts.dev/openapi-fetch/) + openapi-typescript | the typed API client | types generated from the same OpenAPI document as the CLI's client. | Returns errors as data; we unwrap them into exceptions for Query. |
| [shadcn/ui](https://ui.shadcn.com/) (Radix) + Tailwind CSS | UI components | accessible primitives copied into the repo, so we own and adapt them. | Generated components are committed; icons are switched to Material Symbols. |
| [Recharts](https://recharts.org/) (MIT) | resource usage graphs (ADR-0017) | what shadcn's `chart` component wraps, so graphs follow the panel's themes through CSS variables; declarative React components with tooltips and reference lines. uPlot is smaller and faster but would need its own theming and React wrapper; Chart.js draws on a canvas that CSS variables don't reach. | Uses Redux Toolkit internally; that is the library's own state, not the panel's (the no-Redux rule stands). Loaded only with the metrics screens (a lazy chunk of about 110 KB gzipped). Animations are off: every refetch would replay them. |
