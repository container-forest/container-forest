# Робота над container-forest

Правила для команди: як вносити зміни, як влаштований репозиторій і як запустити систему локально. Технічний опис системи — у [docs/architecture.md](docs/architecture.md).

## Правила роботи в команді

- Кожен PR перевіряється на [інваріанти безпеки](docs/architecture.md#інваріанти-безпеки): порушувати їх не можна в жодному PR.
- Жодних прямих комітів у `main`. Лише PR з хоча б одним рев'ю від іншого учасника і зеленим [CI](#ci).
- Коміти за [Conventional Commits](https://www.conventionalcommits.org/): `feat(agent): ...`, `fix(web): ...`.
- **`CONTRIBUTIONS.md`** — кожен веде свій список класів, методів, ієрархій, екранів і обробників для лаб. Оновлюйте разом із PR, а не в кінці семестру. Попередній розподіл класів і екранів між учасниками — у [docs/project-overview.md](docs/project-overview.md#команда).
- Нотатки для ретроспективи з лаби 3 пишіть у `docs/retro/<бібліотека>.md` одразу, поки пам'ятаєте проблеми. Якщо з бібліотекою працює кілька учасників, кожен пише свій розділ у тому самому файлі.
- Архітектурні рішення, про які сперечались (наприклад, «git тягне ЦА, а не агент»), фіксуйте короткими ADR у `docs/adr/`.
- Аналіз аналогів у `docs/project-overview.md` оновлюється на кожному етапі проєкту новим підрозділом з датою; стан на початку розробки не редагується.

## Структура репозиторію

Обґрунтування — у [ADR-0001](docs/adr/0001-solution-structure.md).

```
ContainerForest.slnx
.github/workflows/ci.yml         CI: збірка й тести на кожен PR
Directory.Build.props            спільні властивості .NET-проєктів
Directory.Packages.props         версії NuGet-пакетів (central package management)
dotnet-tools.json                локальні інструменти .NET (dotnet-ef)
buf.yaml                         правила лінтера для .proto (Buf)
src/
  Contracts/                     .proto-файли; C#-код генерують проєкти-споживачі
  Aggregator/
    Domain/                      доменна модель, без залежностей
    Application/                 команди, авторизація, пайплайн деплою, порти
    Infrastructure/              EF Core, git, SOPS, з'єднання з агентами
    Api/                         ASP.NET Core: REST, SignalR, gRPC; openapi/*.json
  Agent/                         .NET Worker Service
  DiscordBot/
  Web/                           Vite + React + TS
tests/                           дзеркалить src/
deploy/                          compose-файли для ЦА і агента
docs/
  architecture.md                компоненти, протоколи, безпека, права
  project-overview.md            опис проєкту, команда, аналіз аналогів
  adr/                           записи архітектурних рішень
  retro/                         ретроспективи по бібліотеках (лаба 3)
CONTRIBUTING.md                  цей файл: правила команди
CONTRIBUTIONS.md                 облік елементів лабораторних для кожного учасника
```

## Іменування

| Де | Як |
|---|---|
| Docker-образи | `container-forest-aggregator`, `container-forest-agent`, `container-forest-web`, `container-forest-discord-bot` |
| .NET-проєкти й namespace'и | `ContainerForest.*` |
| Protobuf-пакети | `<модуль>` без префікса й версії; файл у `src/Contracts/<модуль>/` (ADR-0002) |
| Змінні середовища | префікс `CF_`, вкладені секції через `__` (наприклад, `CF_AGENT__CONTROLLERURL`) |
| Каталоги на хостах | `/opt/container-forest` (стеки, дані агента), `/run/container-forest` (секрети, tmpfs) |

## Локальний запуск

Потрібно: .NET 10 SDK, Node.js 22.12+ (у CI і образі — 24, `src/Web/.nvmrc`), Docker (на Windows — Docker Desktop).

1. `dotnet run --project src/Aggregator/Api` — ЦА: REST на `http://localhost:5080`, gRPC для агентів на `http://localhost:5081`. База SQLite створюється й мігрує сама при старті (`src/Aggregator/Api/aggregator.db`).
2. Створити середовище й отримати enrollment-токен. Поки немає логіну й панелі, для цього є тимчасові dev-ендпоінти, доступні лише в оточенні `Development`. Замість логіну заголовок `X-Dev-User` називає користувача, від імені якого йде запит (без пароля):
   ```sh
   curl -X POST http://localhost:5080/api/dev/users -H 'content-type: application/json' -d '{"userName":"root","isAdmin":true}'   # без заголовка
   curl -H 'X-Dev-User: root' http://localhost:5080/api/me
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments -H 'content-type: application/json' -d '{"name":"local","isPrivate":false}'
   curl -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments                   # видимі середовища й статус агентів
   curl -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments/<id>/containers   # видимі контейнери через агента
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments/<id>/containers/<container>/restart   # start | stop | restart, ?timeoutSeconds=30; <container> — повний id, ім'я або унікальний префікс id (як у docker)
   ```
   Роль на середовищі для звичайного користувача: `POST /api/dev/users/<userId>/environment-roles` з `{"environmentId":"<id>","role":"Operator"}`.
   Деплой стеку з публічного git-репозиторію (compose-файл має використовувати готові образи, `build:` відхиляється; поки агент не вміє `DeployStack`, деплой завершиться помилкою на останньому кроці):
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/repositories -H 'content-type: application/json' -d '{"name":"infra","url":"https://github.com/<org>/<repo>.git"}'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments/<id>/stacks -H 'content-type: application/json' -d '{"name":"web","repositoryId":"<repoId>","branch":"main","composePath":"apps/web/compose.yaml"}'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/stacks/<stackId>/deployments -H 'content-type: application/json' -d '{"pull":false}'   # pull=true — redeploy with pull
   curl -H 'X-Dev-User: root' http://localhost:5080/api/dev/deployments/<deploymentId>   # статус і лог
   ```
   Клони репозиторіїв ЦА тримає в `git-cache/` у робочому каталозі (`CF_GIT__CACHEDIRECTORY`).

   Discord-бот підключається до того самого gRPC-порту `5081`. ЦА приймає його, лише якщо в конфігурації є хеш токена бота:
   ```sh
   TOKEN=$(openssl rand -hex 32)                                              # у бота: CF_BOT__SERVICETOKEN
   export CF_BOTGATEWAY__TOKENHASHES__0=$(printf '%s' "$TOKEN" | sha256sum | cut -d' ' -f1)   # у ЦА, перед dotnet run
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/discord/link-code          # код для /link у Discord
   ```
3. `dotnet run --project src/Agent` з `CF_AGENT__ENROLLMENTTOKEN=<токен>` — агент зареєструється, підключиться до ЦА і до локального Docker. У `Development` адреса ЦА вже `http://localhost:5081` (інакше — `CF_AGENT__CONTROLLERURL`). Облікові дані агент зберігає в `agent-credentials.json` у каталозі даних (`CF_AGENT__DATADIRECTORY`; типово `/opt/container-forest`, на Windows `%LOCALAPPDATA%\container-forest`), тож токен потрібен лише для першого запуску. Щоб зареєструвати агента наново, видаліть цей файл. У PowerShell:
   ```powershell
   $env:CF_AGENT__ENROLLMENTTOKEN = "<токен>"
   dotnet run --project src/Agent
   ```
4. `dotnet run --project src/DiscordBot` — Discord-бот. Адреса ЦА в `Development` вже `http://localhost:5081` (інакше — `CF_BOT__CONTROLLERURL`). Токен із кроку 2 бот бере з `CF_BOT__SERVICETOKEN`, локально — з user-secrets:
   ```sh
   dotnet user-secrets set Bot:ServiceToken "$TOKEN" --project src/DiscordBot
   ```
   У PowerShell токен і хеш для ЦА (`sha256sum` тут немає):
   ```powershell
   $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   $token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
   $env:CF_BOTGATEWAY__TOKENHASHES__0 = -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') })   # у ЦА, перед dotnet run
   dotnet user-secrets set Bot:ServiceToken $token --project src/DiscordBot
   ```
   Токен рівносильний адмінському доступу; деталі й ротація — у `CLAUDE.md` (пункт про токен бота).

   Для Discord потрібен власний тестовий застосунок (https://discord.com/developers/applications → Bot → Reset Token) і тестовий сервер, куди бота запрошено зі scope `bot` і `applications.commands` (OAuth2 → URL Generator). Id сервера: у Discord увімкнути Developer Mode, потім ПКМ на сервері → Copy Server ID. У `Development` slash-команди реєструються лише на цьому сервері, де з'являються одразу:
   ```sh
   dotnet user-secrets set Discord:Token "<токен бота Discord>" --project src/DiscordBot
   dotnet user-secrets set Discord:DevGuildId "<id сервера>" --project src/DiscordBot
   ```
   Щоб користуватися командами, прив'яжіть Discord-акаунт: отримайте код (`curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/discord/link-code`) і виконайте в Discord `/link <код>`. Відповіді бота бачить лише автор команди.
5. Панель: `cd src/Web && npm ci`, потім `CF_DEV_USER=root npm run dev` (PowerShell: `$env:CF_DEV_USER="root"; npm run dev`) — `http://localhost:5173`. Dev-сервер проксює `/api` і `/hubs` на ЦА (`CF_AGGREGATOR_URL`, типово `http://localhost:5080`) і, поки немає логіну, додає до кожного запиту `X-Dev-User` з `CF_DEV_USER`. Без неї панель відправить на `/login`.
   - Змінили DTO в ЦА — зберіть `src/Aggregator/Api` (оновиться OpenAPI-документ) і виконайте `npm run generate:api`: типи клієнта в `src/api/schema.gen.ts` генеруються з нього.
   - `src/routeTree.gen.ts` генерує плагін TanStack Router під час `npm run dev`/`build`; обидва згенеровані файли комітяться й руками не редагуються.
   - Компоненти UI — shadcn/ui: `npx shadcn@latest add table` (з `src/Web`) додає компонент у `src/components/ui`; згенеровані файли комітяться.
   - Docker-образ панелі (nginx зі статичними файлами, проксі `/api` і `/hubs` на `AGGREGATOR_URL`): `docker build -t container-forest-web:dev src/Web`.

Тести: `dotnet test --solution ContainerForest.slnx`. Інтеграційні тести в `tests/Aggregator/Api` піднімають ЦА в пам'яті й підключаються до нього як агент. Тести в `tests/DiscordBot` піднімають у пам'яті gRPC-сервер `BotGateway` із заготовленими відповідями.

Docker-образ агента збирається з кореня репозиторію (контексту потрібні `src/Contracts` і `Directory.*.props`), а запускається так само, як у [прикладі розгортання](docs/architecture.md#розгортання-агента). Щоб підключити його до ЦА, запущеного локально через `dotnet run`, використовуйте `http://host.docker.internal:5081`:

```sh
docker build -f src/Agent/Dockerfile -t container-forest-agent:dev .
docker run -d --name cf-agent --hostname my-laptop \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v cf-agent-data:/opt/container-forest \
  -e CF_AGENT__CONTROLLERURL=http://host.docker.internal:5081 \
  -e CF_AGENT__ENROLLMENTTOKEN=<токен> \
  container-forest-agent:dev
```

Міграції EF Core (інструмент ставиться з `dotnet-tools.json`):

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Aggregator/Infrastructure --output-dir Persistence/Migrations
```

⚠️ Для розробки використовуйте локальний Docker або тестовий сервер, **не продакшн-інфраструктуру**.

## CI

GitHub Actions (`.github/workflows/ci.yml`) запускається на кожен PR і на пуш у `main`. PR мержиться лише із зеленим CI. Кроки:

1. `dotnet restore` і `dotnet build` з `CI=true`: попередження компілятора й аналізаторів стають помилками (`Directory.Build.props`).
2. Перевірка, що закомічений `src/Aggregator/Api/openapi/*.json` збігається з тим, що згенерувала збірка. Якщо крок упав — зберіть `src/Aggregator/Api` локально й закомітьте JSON.
3. `dotnet test --solution ContainerForest.slnx`.
4. Окремий job для панелі (`src/Web`): `npm ci` → перевірка, що `src/api/schema.gen.ts` збігається з OpenAPI-документом → `npm run build` (перевірка типів і збірка) → перевірка, що `src/routeTree.gen.ts` закомічений актуальним.

Відтворити CI локально перед пушем:

```sh
CI=true dotnet build ContainerForest.slnx     # PowerShell: $env:CI="true"; dotnet build ContainerForest.slnx
git status src/Aggregator/Api/openapi          # має бути без змін
dotnet test --solution ContainerForest.slnx --no-build
cd src/Web && npm run generate:api && npm run build && git status .   # має бути без змін
```

Тест, що впав у CI, — це помилка, яку треба знайти, а не «нестабільність»: не вимикайте й не пропускайте тести, щоб отримати зелений статус.
