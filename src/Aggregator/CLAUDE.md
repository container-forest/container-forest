# Центральний агрегатор: конвенції

Описи підсистем — у [docs/design/](../../docs/design/README.md); прочитайте потрібний перед зміною.

## Домен і сервіси

- Доменні класи називаються `DockerEnvironment` (не `Environment`: конфлікт із `System.Environment` через неявний `using System`) і `GitRepository` (не `Repository`: плутається з патерном Repository).
- Домен не читає годинник сам: методи приймають `DateTimeOffset now`. Ідентифікатори — `readonly record struct` над `Guid` v7 з інтерфейсом `IStronglyTypedId<TSelf>`.
- Кожна операція з БД у довгому gRPC-виклику `Connect` — у власному DI-scope: один `DbContext` на години виклику тримав би застарілі сутності.
- Сервісний метод, що змінює сам обліковий запис або видає облікові дані, бере користувача через `CurrentActor.GetSignedInUserAsync` ([auth](../../docs/design/auth.md)).
- Налаштування необов'язкової функції, яке змінюють із панелі, — секція `SettingsSection`, а не нове поле options ([налаштування](../../docs/design/backup.md)).
- Дія, яку треба бачити в журналі, проходить через `IAuditTrail` ([audit](../../docs/design/audit.md)). Новий запис у БД, що має бути видно наживо, — випадок у `LiveChanges.Collect` ([live-оновлення](../../docs/design/live-updates.md)).

## З'єднання з агентами

- `AgentRegistry.Open` при повторному підключенні агента закриває стару сесію. При закритті сесії всі очікувані запити завершуються `AgentDisconnectedException`.
- ЦА надсилає `CancelRequest` у `AgentSession.RequestAsync`, коли перестає чекати результат (тайм-аут або скасування викликача) і сесія жива. Це best effort з власним тайм-аутом запису 5 с: помилка надсилання не підміняє `AgentTimeoutException`. Без цього compose після 15-хвилинного тайм-ауту деплою й далі змінював би стек.
- gRPC обмежує повідомлення 4 МБ за замовчуванням: задати явний ліміт розміру пакета деплою на ЦА і відхиляти більші.

## Хостинг

- Образ ЦА — `src/Aggregator/Api/Dockerfile` (контекст — корінь репозиторію), база `aspnet:10.0`, непривілейований користувач. Усі дані — у `/data` (`aggregator.db`, `data-protection-keys/`, `git-cache/`, задані `CF_*` в образі); томом бекапиться весь каталог. OpenAPI-документ під час збірки образу не генерується (`OpenApiGenerateDocuments=false`): він закомічений і перевіряється CI. Розгортання: `deploy/aggregator/compose.yaml` (ЦА + панель-Caddy, налаштування в `.env`, яке цілком передається ЦА як `env_file`, тож незадана змінна лишається незаданою, а не порожньою), `deploy/agent/compose.yaml`, `deploy/discord-bot/compose.yaml`. Образи — `ghcr.io/lepterion/container-forest-*`, збирає й публікує workflow `Images`: з `main` — теги `main` і `<sha>`, з релізу — `X.Y.Z`, `X.Y` і `latest` ([Релізи](../../docs/development.md#releases)).
- Cookie-автентифікація для API повертає 401/403 замість редіректу на логін. За reverse proxy — заголовки `X-Forwarded-*` через `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (`deploy/aggregator/compose.yaml`): довіра всім проксі безпечна лише тому, що ЦА не публікує портів і доступний тільки через Caddy.
- Kestrel: окремий порт для gRPC (HTTP/2), окремий для REST/SignalR. Локально 5081 і 5080 на `localhost`; у `appsettings.json` — `+`, розробка перекриває це в `appsettings.Development.json`, щоб dev-ендпоінти не були видні в мережі.
- Reverse proxy перед ЦА має пропускати HTTP/2 для gRPC і не мати таймаутів на довгі стріми, інакше з'єднання рватиметься (у nginx — кожні 60 с). Caddy за ADR-0005 робить це без налаштувань (`h2c://`, `flush_interval -1`).

## Пастки

- Генератор OpenAPI під час збірки (`GetDocument.Insider`) запускає хост **разом із hosted-сервісами**. Тому в `Program.cs` міграції вимикаються, коли entry assembly — `GetDocument.Insider`; інакше збірка створює базу. З тієї ж причини в цьому режимі не підключається Data Protection з файловим key ring (ефемерний провайдер), інакше збірка створює `data-protection-keys/`.
- Key ring Data Protection (`DataProtection:KeyDirectory`, типово `data-protection-keys`) — єдиний спосіб розшифрувати збережені секрети: бекапити разом із базою ([бекап](../../docs/design/backup.md)), у контейнері ЦА монтувати томом. Втрачений key ring — `SecretUnreadableException`, креди треба задати заново.
- SQLite не має типу `DateTimeOffset`: як текст його не можна порівнювати й сортувати в запитах. Усі `DateTimeOffset` зберігаються як `long` (`DateTimeOffsetToBinaryConverter`, конвенція в `AggregatorDbContext`).
- Вирази EF (`ValueConverter`) не можуть викликати `static abstract` члени інтерфейсу напряму — лише через звичайний статичний метод (див. `StronglyTypedIdConverter`).

## Library notes

Pitfalls of the libraries the aggregator uses that aren't covered in `docs/design/`. Why each library was chosen: [docs/dependencies.md](../../docs/dependencies.md).

- **ASP.NET Core Identity.** The cookie scheme redirects to `/Account/Login` by default; `OnRedirectToLogin` and `OnRedirectToAccessDenied` are overridden to return 401 and 403. `IdentityResult` doesn't say which errors are about the password: we recognise them by the `Password` code prefix, so custom `IPasswordValidator`s must use codes with that prefix.
- **Discord OAuth.** A `SameSite=Strict` session cookie set during a redirect chain that started on discord.com isn't sent on the next request, so the last step answers with a `meta refresh` page: the following navigation starts on our site.
- **AWSSDK.S3.** Over HTTPS the SDK signs uploads as streaming SigV4, which Cloudflare R2 rejects, and over plain HTTP it behaves differently, so an HTTP-only test passes anyway. Test against HTTPS. Newer SDKs also add checksums some compatible servers don't know: `RequestChecksumCalculation` and `ResponseChecksumValidation` are `WHEN_REQUIRED`. Compatible servers need `ServiceURL`, `ForcePathStyle` and `AuthenticationRegion` (R2's `auto` isn't an AWS region).
- **EF Core.** Two TPH subclasses with the same property (`EnvironmentId` on environment and stack roles) share one column via `HasColumnName`; the unique index is filtered by discriminator. Case-insensitive unique names (environments, repositories, users) use the `NOCASE` collation, not normalisation in code. Owned collections come back in no particular order: deployment log lines carry a `Sequence`.
- **gRPC.** A server `Interceptor` that only overrides `UnaryServerHandler` lets streaming calls through untouched: authentication interceptors override all four call kinds. `TestServer` applies a client's deadline itself and drops the request before the client's timer fires, so the client sees `Internal`, not `DeadlineExceeded`; it also notices client cancellation only after the server has sent response headers.
- **LibGit2Sharp.** `Commands.Fetch` takes a remote name, not a URL: the cache clone keeps a remote `origin` whose URL is updated before every fetch. The API is synchronous: fetches run in `Task.Run`, and concurrent deployments of one repository wait on a `SemaphoreSlim` (libgit2 can't write to a repository from two threads). File sizes are checked with `Blob.Size` before reading the content. The credentials callback gets the URL of the current request, which a server can redirect: it hands out credentials only over HTTPS, for the repository's host and port, for paths under the git connection's prefix, and refuses any `%`-encoding in the path. The token never goes into a URL. libgit2's authentication errors ("no callback set", "too many redirects or authentication replays") get a hint appended about the token.
- **LibGit2Sharp and signatures.** `Commit.ExtractSignature` returns the signed data as a string: re-encode it as UTF-8 to verify. An unsigned commit throws `NotFoundException` rather than returning `null`. "Is a descendant of" is `ObjectDatabase.FindMergeBase(commit, ancestor) == ancestor`. Tests fetch from a local path instead of a URL; their cleanup must not follow symbolic links.
- **YamlDotNet.** Compose files are read as a node tree (`RepresentationModel`), not a typed model. `YamlStream` resolves anchors but leaves `<<` as an ordinary key, so key lookups check the service first and then its merged maps. `YamlException.Message` has no position: take line and column from `YamlException.Start`. Images with `${VAR}` are known only after interpolation on the host and aren't checked.
