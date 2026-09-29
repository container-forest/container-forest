# Робота над container-forest

Правила для команди: як вносити зміни, як влаштований репозиторій і як запустити систему локально. Технічний опис системи — у [docs/architecture.md](docs/architecture.md).

## Правила роботи в команді

- Кожен PR перевіряється на [інваріанти безпеки](docs/architecture.md#інваріанти-безпеки): порушувати їх не можна в жодному PR.
- Жодних прямих комітів у `main`. Лише PR з хоча б одним рев'ю від іншого учасника.
- Коміти за [Conventional Commits](https://www.conventionalcommits.org/): `feat(agent): ...`, `fix(web): ...`.
- **`CONTRIBUTIONS.md`** — кожен веде свій список класів, методів, ієрархій, екранів і обробників для лаб. Оновлюйте разом із PR, а не в кінці семестру. Попередній розподіл класів і екранів між учасниками — у [docs/project-overview.md](docs/project-overview.md#команда).
- Нотатки для ретроспективи з лаби 3 пишіть у `docs/retro/<бібліотека>.md` одразу, поки пам'ятаєте проблеми. Якщо з бібліотекою працює кілька учасників, кожен пише свій розділ у тому самому файлі.
- Архітектурні рішення, про які сперечались (наприклад, «git тягне ЦА, а не агент»), фіксуйте короткими ADR у `docs/adr/`.
- Аналіз аналогів у `docs/project-overview.md` оновлюється на кожному етапі проєкту новим підрозділом з датою; стан на початку розробки не редагується.

## Структура репозиторію

Обґрунтування — у [ADR-0001](docs/adr/0001-solution-structure.md).

```
ContainerForest.slnx
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

Потрібно: .NET 10 SDK, Node.js 22, Docker (на Windows — Docker Desktop).

1. `dotnet run --project src/Aggregator/Api` — ЦА: REST на `http://localhost:5080`, gRPC для агентів на `http://localhost:5081`. База SQLite створюється й мігрує сама при старті (`src/Aggregator/Api/aggregator.db`).
2. Створити середовище й отримати enrollment-токен. Поки немає логіну й панелі, для цього є тимчасові dev-ендпоінти, доступні лише в оточенні `Development`. Замість логіну заголовок `X-Dev-User` називає користувача, від імені якого йде запит (без пароля):
   ```sh
   curl -X POST http://localhost:5080/api/dev/users -H 'content-type: application/json' -d '{"userName":"root","isAdmin":true}'   # без заголовка
   curl -H 'X-Dev-User: root' http://localhost:5080/api/me
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments -H 'content-type: application/json' -d '{"name":"local","isPrivate":false}'
   curl -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments                   # видимі середовища й статус агентів
   curl -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments/<id>/containers   # видимі контейнери через агента
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/dev/environments/<id>/containers/<container>/restart   # start | stop | restart, ?timeoutSeconds=30
   ```
   Роль на середовищі для звичайного користувача: `POST /api/dev/users/<userId>/environment-roles` з `{"environmentId":"<id>","role":"Operator"}`.
3. `dotnet run --project src/Agent` з `CF_AGENT__CONTROLLERURL=http://localhost:5081` і `CF_AGENT__ENROLLMENTTOKEN=<токен>` — агент підключиться до локального Docker.
4. `cd src/Web && npm ci && npm run dev` — панель на `http://localhost:5173`; `/api` і `/hubs` проксюються на ЦА.

Тести: `dotnet test --solution ContainerForest.slnx`. Інтеграційні тести в `tests/Aggregator/Api` піднімають ЦА в пам'яті й підключаються до нього як агент.

Міграції EF Core (інструмент ставиться з `dotnet-tools.json`):

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Aggregator/Infrastructure --output-dir Persistence/Migrations
```

⚠️ Для розробки використовуйте локальний Docker або тестовий сервер, **не продакшн-інфраструктуру**.
