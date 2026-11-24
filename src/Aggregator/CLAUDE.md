# Aggregator: conventions

Subsystem descriptions are in [docs/design/](../../docs/design/README.md); read the relevant one before changing it.

## Domain and services

- Domain classes are called `DockerEnvironment` (not `Environment`: it clashes with `System.Environment` through the implicit `using System`) and `GitRepository` (not `Repository`: confused with the Repository pattern).
- The domain never reads the clock itself: methods take `DateTimeOffset now`. Ids are `readonly record struct`s over `Guid` v7 implementing `IStronglyTypedId<TSelf>`.
- Every database operation in the long `Connect` gRPC call runs in its own DI scope: one `DbContext` for hours of a call would hold stale entities.
- A service method that changes the account itself or issues credentials gets the user through `CurrentActor.GetSignedInUserAsync` ([auth](../../docs/design/auth.md)).
- A setting of an optional feature that is changed from the panel is a `SettingsSection`, not a new options field ([settings](../../docs/design/backup.md)).
- An action that must show up in the log goes through `IAuditTrail` ([audit](../../docs/design/audit.md)). A new database record that must be visible live is a case in `LiveChanges.Collect` ([live updates](../../docs/design/live-updates.md)).

## Agent connections

- When an agent reconnects, `AgentRegistry.Open` closes the old session. Closing a session completes all pending requests with `AgentDisconnectedException`.
- The aggregator sends `CancelRequest` in `AgentSession.RequestAsync` when it stops waiting for a result (a timeout or the caller's cancellation) and the session is alive. It's best effort with its own 5 s write timeout: a failed send doesn't replace `AgentTimeoutException`. Without it, compose would keep changing the stack after the 15-minute deployment timeout.
- gRPC limits messages to 4 MB by default: set an explicit deployment bundle size limit on the aggregator and reject larger bundles.

## Hosting

- The aggregator's image is `src/Aggregator/Api/Dockerfile` (built from the repository root), based on `aspnet:10.0`, running as an unprivileged user. All data is in `/data` (`aggregator.db`, `data-protection-keys/`, `git-cache/`, set by `CF_*` in the image); back up the whole directory as a volume. The OpenAPI document isn't generated during the image build (`OpenApiGenerateDocuments=false`): it's committed and checked by CI. Deployment: `deploy/aggregator/compose.yaml` (the aggregator plus the Caddy panel, settings in `.env`, passed to the aggregator whole as `env_file`, so an unset variable stays unset rather than empty), `deploy/agent/compose.yaml`, `deploy/discord-bot/compose.yaml`. Images are `ghcr.io/container-forest/container-forest-*`, built and published by the `Images` workflow: from `main` with the tags `main` and `<sha>`, from a release with `X.Y.Z`, `X.Y` and `latest` ([Releases](../../docs/development.md#releases)).
- Cookie authentication for the API returns 401/403 instead of redirecting to a login page. Behind the reverse proxy, `X-Forwarded-*` headers are trusted through `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (`deploy/aggregator/compose.yaml`): trusting every proxy is safe only because the aggregator publishes no ports and is reachable only through Caddy.
- Kestrel: one port for gRPC (HTTP/2), another for REST/SignalR. Locally 5081 and 5080 on `localhost`; `appsettings.json` uses `+`, and development overrides it in `appsettings.Development.json` so dev endpoints aren't visible on the network.
- A reverse proxy in front of the aggregator must pass HTTP/2 for gRPC and have no timeouts on long streams, or connections will drop (every 60 s in nginx). Caddy (ADR-0005) does this without configuration (`h2c://`, `flush_interval -1`).

## Pitfalls

- The OpenAPI generator during the build (`GetDocument.Insider`) starts the host **together with hosted services**. That's why `Program.cs` turns migrations off when the entry assembly is `GetDocument.Insider`; otherwise the build creates a database. For the same reason, Data Protection with a file key ring isn't wired up in that mode (an ephemeral provider is used), or the build would create `data-protection-keys/`.
- The Data Protection key ring (`DataProtection:KeyDirectory`, `data-protection-keys` by default) is the only way to decrypt stored secrets: back it up with the database ([backup](../../docs/design/backup.md)) and mount it as a volume in the aggregator's container. A lost key ring means `SecretUnreadableException`, and credentials have to be entered again.
- SQLite has no `DateTimeOffset` type: as text it can't be compared or sorted in queries. Every `DateTimeOffset` is stored as a `long` (`DateTimeOffsetToBinaryConverter`, a convention in `AggregatorDbContext`).
- EF expressions (`ValueConverter`) can't call `static abstract` interface members directly, only through an ordinary static method (see `StronglyTypedIdConverter`).

## Library notes

Pitfalls of the libraries the aggregator uses that aren't covered in `docs/design/`. Why each library was chosen: [docs/dependencies.md](../../docs/dependencies.md).

- **AWSSDK.S3.** Besides streaming signing ([design/backup.md](../../docs/design/backup.md)), newer SDKs add checksums some compatible servers don't know: `RequestChecksumCalculation` and `ResponseChecksumValidation` are `WHEN_REQUIRED`. Compatible servers need `ServiceURL`, `ForcePathStyle` and `AuthenticationRegion` (R2's `auto` isn't an AWS region).
- **EF Core.** Two TPH subclasses with the same property (`EnvironmentId` on environment and stack roles) share one column via `HasColumnName`; the unique index is filtered by discriminator. Case-insensitive unique names (environments, repositories, users) use the `NOCASE` collation, not normalisation in code. Owned collections come back in no particular order: deployment log lines carry a `Sequence`.
- **gRPC.** `TestServer` applies a client's deadline itself and drops the request before the client's timer fires, so the client sees `Internal`, not `DeadlineExceeded`; it also notices client cancellation only after the server has sent response headers.
- **LibGit2Sharp.** `Commands.Fetch` takes a remote name, not a URL: the cache clone keeps a remote `origin` whose URL is updated before every fetch. The API is synchronous: fetches run in `Task.Run`, and concurrent deployments of one repository wait on a `SemaphoreSlim` (libgit2 can't write to a repository from two threads). File sizes are checked with `Blob.Size` before reading the content. The credentials callback gets the URL of the current request, which a server can redirect: it hands out credentials only over HTTPS, for the repository's host and port, for paths under the git connection's prefix, and refuses any `%`-encoding in the path. The token never goes into a URL. libgit2's authentication errors ("no callback set", "too many redirects or authentication replays") get a hint appended about the token.
- **LibGit2Sharp and signatures.** `Commit.ExtractSignature` returns the signed data as a string: re-encode it as UTF-8 to verify. An unsigned commit throws `NotFoundException` rather than returning `null`. "Is a descendant of" is `ObjectDatabase.FindMergeBase(commit, ancestor) == ancestor`. Tests fetch from a local path instead of a URL; their cleanup must not follow symbolic links.
- **YamlDotNet.** Compose files are read as a node tree (`RepresentationModel`), not a typed model. `YamlStream` resolves anchors but leaves `<<` as an ordinary key, so key lookups check the service first and then its merged maps. `YamlException.Message` has no position: take line and column from `YamlException.Start`. Images with `${VAR}` are known only after interpolation on the host and aren't checked.
