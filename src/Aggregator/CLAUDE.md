# Центральний агрегатор: конвенції

Описи підсистем — у [docs/design/](../../docs/design/README.md); прочитайте потрібний перед зміною.

## Домен і сервіси

- Доменні класи називаються `DockerEnvironment` (не `Environment`: конфлікт із `System.Environment` через неявний `using System`) і `GitRepository` (не `Repository`: плутається з патерном Repository).
- Домен не читає годинник сам: методи приймають `DateTimeOffset now`. Ідентифікатори — `readonly record struct` над `Guid` v7 з інтерфейсом `IStronglyTypedId<TSelf>`.
- Кожна операція з БД у довгому gRPC-виклику `Connect` — у власному DI-scope: один `DbContext` на години виклику тримав би застарілі сутності.
- Сервісний метод, що змінює сам обліковий запис або видає облікові дані, бере користувача через `CurrentActor.GetSignedInUserAsync` ([auth](../../docs/design/auth.md)).
- Дія, яку треба бачити в журналі, проходить через `IAuditTrail` ([audit](../../docs/design/audit.md)). Новий запис у БД, що має бути видно наживо, — випадок у `LiveChanges.Collect` ([live-оновлення](../../docs/design/live-updates.md)).

## З'єднання з агентами

- `AgentRegistry.Open` при повторному підключенні агента закриває стару сесію. При закритті сесії всі очікувані запити завершуються `AgentDisconnectedException`.
- ЦА надсилає `CancelRequest` у `AgentSession.RequestAsync`, коли перестає чекати результат (тайм-аут або скасування викликача) і сесія жива. Це best effort з власним тайм-аутом запису 5 с: помилка надсилання не підміняє `AgentTimeoutException`. Без цього compose після 15-хвилинного тайм-ауту деплою й далі змінював би стек.
- gRPC обмежує повідомлення 4 МБ за замовчуванням: задати явний ліміт розміру пакета деплою на ЦА і відхиляти більші.

## Хостинг

- Образ ЦА — `src/Aggregator/Api/Dockerfile` (контекст — корінь репозиторію), база `aspnet:10.0`, непривілейований користувач. Усі дані — у `/data` (`aggregator.db`, `data-protection-keys/`, `git-cache/`, задані `CF_*` в образі); томом бекапиться весь каталог. OpenAPI-документ під час збірки образу не генерується (`OpenApiGenerateDocuments=false`): він закомічений і перевіряється CI. Розгортання: `deploy/aggregator/compose.yaml` (ЦА + панель-Caddy, налаштування в `.env`, яке цілком передається ЦА як `env_file`, тож незадана змінна лишається незаданою, а не порожньою), `deploy/agent/compose.yaml`, `deploy/discord-bot/compose.yaml`. Образи — `ghcr.io/lepterion/container-forest-*`, збирає й публікує workflow `Images` з `main`.
- Cookie-автентифікація для API повертає 401/403 замість редіректу на логін. За reverse proxy — заголовки `X-Forwarded-*` через `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (`deploy/aggregator/compose.yaml`): довіра всім проксі безпечна лише тому, що ЦА не публікує портів і доступний тільки через Caddy.
- Kestrel: окремий порт для gRPC (HTTP/2), окремий для REST/SignalR. Локально 5081 і 5080 на `localhost`; у `appsettings.json` — `+`, розробка перекриває це в `appsettings.Development.json`, щоб dev-ендпоінти не були видні в мережі.
- Reverse proxy перед ЦА має пропускати HTTP/2 для gRPC і не мати таймаутів на довгі стріми, інакше з'єднання рватиметься (у nginx — кожні 60 с). Caddy за ADR-0005 робить це без налаштувань (`h2c://`, `flush_interval -1`).

## Пастки

- Генератор OpenAPI під час збірки (`GetDocument.Insider`) запускає хост **разом із hosted-сервісами**. Тому в `Program.cs` міграції вимикаються, коли entry assembly — `GetDocument.Insider`; інакше збірка створює базу. З тієї ж причини в цьому режимі не підключається Data Protection з файловим key ring (ефемерний провайдер), інакше збірка створює `data-protection-keys/`.
- Key ring Data Protection (`DataProtection:KeyDirectory`, типово `data-protection-keys`) — єдиний спосіб розшифрувати збережені секрети: бекапити разом із базою, у контейнері ЦА монтувати томом. Втрачений key ring — `SecretUnreadableException`, креди треба задати заново.
- SQLite не має типу `DateTimeOffset`: як текст його не можна порівнювати й сортувати в запитах. Усі `DateTimeOffset` зберігаються як `long` (`DateTimeOffsetToBinaryConverter`, конвенція в `AggregatorDbContext`).
- Вирази EF (`ValueConverter`) не можуть викликати `static abstract` члени інтерфейсу напряму — лише через звичайний статичний метод (див. `StronglyTypedIdConverter`).
