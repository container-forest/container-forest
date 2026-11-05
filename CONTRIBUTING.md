# Робота над container-forest

Правила для команди: як вносити зміни, як влаштований репозиторій і як запустити систему локально. Технічний опис системи — у [docs/architecture.md](docs/architecture.md).

## Правила роботи в команді

- Кожен PR перевіряється на [інваріанти безпеки](docs/architecture.md#інваріанти-безпеки): порушувати їх не можна в жодному PR.
- Жодних прямих комітів у `main`. Лише PR із зеленим [CI](#ci), відкритий у `main` (не в гілку іншого PR).
- Рев'ю (рішення команди): кожен PR Бояра В. О., Синенка Д. В. і Денисенко Л. В. мерджиться лише після аппруву Петренка Б. О.; GitHub додає його рев'юером автоматично (`.github/CODEOWNERS`). Свої PR Петренко Б. О. перевіряє сам.
- Коміти за [Conventional Commits](https://www.conventionalcommits.org/): `feat(agent): ...`, `fix(web): ...`.
- **`CONTRIBUTIONS.md`** — кожен веде свій список класів, методів, ієрархій, екранів і обробників для лаб. Оновлюйте разом із PR, а не в кінці семестру. Попередній розподіл класів і екранів між учасниками — у [docs/project-overview.md](docs/project-overview.md#команда).
- Нотатки для ретроспективи з лаби 3 пишіть у `docs/retro/<бібліотека>.md` одразу, поки пам'ятаєте проблеми. Якщо з бібліотекою працює кілька учасників, кожен пише свій розділ у тому самому файлі.
- Архітектурні рішення, про які сперечались (наприклад, «git тягне ЦА, а не агент»), фіксуйте короткими ADR у `docs/adr/`.
- Аналіз аналогів у `docs/project-overview.md` оновлюється на кожному етапі проєкту новим підрозділом з датою; стан на початку розробки не редагується.

## Структура репозиторію

Обґрунтування — у [ADR-0001](docs/adr/0001-solution-structure.md).

```
ContainerForest.slnx
.editorconfig                    стиль коду (конвенції .NET для C#)
.github/workflows/ci.yml         CI: збірка й тести на кожен PR
.github/workflows/docs.yml       сайт документації (DocFX) → GitHub Pages
.github/workflows/images.yml     Docker-образи: збірка на PR, публікація в GHCR з main
.github/workflows/cli.yml        CLI forest: AOT-збірка на PR, бінарники й реліз за тегом cli-v*
docfx.json, toc.yml, index.md    конфігурація сайту документації
scripts/install-sops.sh          sops і age-keygen для образу ЦА й CI (версії й контрольні суми)
scripts/generate-cli-client.sh   клієнт API для CLI (Kiota) з OpenAPI-документа ЦА
Directory.Build.props            спільні властивості .NET-проєктів
Directory.Packages.props         версії NuGet-пакетів (central package management)
dotnet-tools.json                локальні інструменти .NET (dotnet-ef, docfx)
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
  Cli/                           CLI forest (NativeAOT); Api/Generated — клієнт Kiota
  Web/                           Vite + React + TS
tests/                           дзеркалить src/
deploy/                          compose-файли для ЦА з панеллю, агента й бота
docs/
  architecture.md                компоненти, протоколи, безпека, права
  project-overview.md            опис проєкту, команда, аналіз аналогів
  adr/                           записи архітектурних рішень
  retro/                         ретроспективи по бібліотеках (лаба 3)
  ai-usage.md                    використання генеративного ШІ (вимога курсу)
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
   Першого адміністратора ЦА створює при старті з `CF_BOOTSTRAP__ADMINUSERNAME` і `CF_BOOTSTRAP__ADMINPASSWORD` (пароль від 12 символів), якщо в базі ще немає користувачів; іншого способу створити його немає. З ним можна ввійти паролем:
   ```sh
   CF_BOOTSTRAP__ADMINUSERNAME=root CF_BOOTSTRAP__ADMINPASSWORD='<пароль>' dotnet run --project src/Aggregator/Api
   curl -c cookies.txt -X POST http://localhost:5080/api/auth/login -H 'content-type: application/json' -d '{"userName":"root","password":"<пароль>","rememberMe":false}'
   curl -b cookies.txt http://localhost:5080/api/me
   ```
2. Створити середовище й отримати enrollment-токен (лише адміністратор). Найпростіше — у панелі (крок 5): «Середовища» → «Нове середовище» показує токен і готовий compose-файл агента. Те саме через API: В оточенні `Development`, крім cookie, заголовок `X-Dev-User` називає користувача, від імені якого йде запит, без пароля: нижче це адміністратор `root` з кроку 1.
   ```sh
   curl -H 'X-Dev-User: root' http://localhost:5080/api/me
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments -H 'content-type: application/json' -d '{"name":"local","isPrivate":false}'   # у відповіді — enrollmentToken, показується лише раз
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/enrollment-token   # новий токен (попередній перестає діяти)
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/revoke-agent       # забути агента й обірвати з'єднання
   curl -H 'X-Dev-User: root' http://localhost:5080/api/environments                   # видимі середовища й статус агентів
   curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/containers   # видимі контейнери через агента
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/containers/<container>/restart   # start | stop | restart, ?timeoutSeconds=30; <container> — повний id, ім'я або унікальний префікс id (як у docker)
   curl -H 'X-Dev-User: root' 'http://localhost:5080/api/environments/<id>/containers/<container>/logs?tail=100'   # останні рядки логу; ?since=<ISO 8601> — лише новіші
   ```
   Користувачі й ролі (API керування користувачами, лише для адміністраторів; повний перелік — `src/Aggregator/Api/Contracts/UserContracts.cs`):
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/users -H 'content-type: application/json' -d '{"userName":"alice","password":"<від 12 символів>","isAdmin":false}'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/users/<userId>/role-assignments -H 'content-type: application/json' -d '{"environmentId":"<id>","stackId":null,"role":"Operator"}'   # stackId — роль лише на стеку
   curl -H 'X-Dev-User: root' http://localhost:5080/api/users/<userId>   # користувач і його ролі
   ```
   API-токени (ADR-0007): токен створює лише сесія (cookie або `X-Dev-User`), показується один раз; `readOnly`, `environmentIds` (null — усі) і `expiresAt` (null — без строку) обмежують його:
   ```sh
   TOKEN=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/tokens -H 'content-type: application/json' -d '{"name":"laptop","readOnly":false,"environmentIds":null,"expiresAt":null}' | jq -r .token)
   curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/me
   curl -X DELETE -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/me/tokens/current   # відкликати сам токен
   ```
   Деплой стеку з git-репозиторію (compose-файл має використовувати готові образи, `build:` відхиляється; для перевірки підходить `https://github.com/dockersamples/example-voting-app.git` з `composePath` = `docker-compose.images.yml`):
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/repositories -H 'content-type: application/json' -d '{"name":"infra","url":"https://github.com/<org>/<repo>.git","connectionId":null}'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/stacks -H 'content-type: application/json' -d '{"name":"web","repositoryId":"<repoId>","branch":"main","composePath":"apps/web/compose.yaml"}'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deployments -H 'content-type: application/json' -d '{"pull":false}'   # pull=true — redeploy with pull
   curl -H 'X-Dev-User: root' http://localhost:5080/api/deployments/<deploymentId>   # статус і лог
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/restart?timeoutSeconds=10   # restart | down; у відповіді — вивід compose
   ```
   Приватний репозиторій — через підключення до git-сервера (ADR-0006, лише адміністратор): префікс адреси, токен лише на читання (Gitea: токен службового користувача зі scope `read:repository`; GitHub: fine-grained token з `Contents: Read-only`; GitLab: deploy token з `read_repository`, тоді `userName` — ім'я токена) і середовища, яким дозволено з нього деплоїти. Токен лише записується: відповідь каже тільки `hasAccessToken`. Репозиторії під підключенням додає будь-хто з роллю Editor у дозволеному середовищі, без токена:
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/git-connections -H 'content-type: application/json' -d '{"name":"team","urlPrefix":"https://gitea.example.com/team/","accessToken":{"userName":null,"token":"<токен>"},"allEnvironments":false,"environmentIds":["<id>"]}'
   curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/git-connections/<connectionId>/access-token -H 'content-type: application/json' -d '{"userName":null,"token":"<новий токен>"}'
   curl -X POST -H 'X-Dev-User: alice' http://localhost:5080/api/repositories -H 'content-type: application/json' -d '{"name":"app","url":"https://gitea.example.com/team/app.git","connectionId":"<connectionId>"}'
   ```
   Публічний репозиторій ЦА не тягне з приватних адрес мережі (loopback, RFC 1918, link-local); для внутрішнього git-сервера створіть підключення (можна без токена) або задайте `CF_GIT__ALLOWPRIVATEHOSTS=true`.
   Клони репозиторіїв ЦА тримає в `git-cache/` у робочому каталозі (`CF_GIT__CACHEDIRECTORY`).

   Деплой після push (опис — у [docs/architecture.md](docs/architecture.md#деплой-після-push-webhook)). Git-сервер має досягати ЦА, тож локально webhook зручно перевіряти підписаним запитом вручну:
   ```sh
   SECRET=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/repositories/<repoId>/webhook | jq -r .secret)   # показується лише раз
   curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deploy-on-push -H 'content-type: application/json' -d '{"enabled":true}'
   BODY='{"ref":"refs/heads/main","after":"<sha коміту>"}'
   curl -X POST http://localhost:5080/api/webhooks/<repoId> -H 'content-type: application/json' -H 'X-GitHub-Event: push' \
     -H "X-Hub-Signature-256: sha256=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -r | cut -d' ' -f1)" -d "$BODY"
   ```

   Хук деплою для CI (опис — у [docs/architecture.md](docs/architecture.md#хук-деплою-для-ci)): токен показується лише раз, ЦА зберігає його хеш:
   ```sh
   HOOK=$(curl -s -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deploy-hook | jq -r .token)
   curl -X POST -H "Authorization: Bearer $HOOK" http://localhost:5080/api/deploy-hooks/<stackId>   # 202: деплой з оновленням образів у черзі
   ```

   Секрети SOPS (опис — у [docs/architecture.md](docs/architecture.md#секрети-в-git-sops)): ЦА потрібні `sops` і `age-keygen` у `PATH`; без них деплой стеку із зашифрованими файлами й створення ключа не вдаються, а решта працює. Поставити ті самі версії, що в образі: `scripts/install-sops.sh ~/.local/bin`. Ключі середовища (лише адміністратор; у відповіді — тільки публічний ключ):
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/age-keys
   curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/age-keys
   sops encrypt --age <recipient>,<ваш ключ> --in-place .env   # або правило в .sops.yaml
   ```
   Git-фільтр (ADR-0010) у тестовому репозиторії: CLI з кроку 6 має бути в `PATH` як `forest` (git запускає його сам), токен — із правом розшифровувати секрети:
   ```sh
   forest login --secrets http://localhost:5173     # у браузері лишити галочку «Розшифрування секретів»
   forest secrets init -e local                      # .sops.yaml, .gitattributes, фільтр і pre-commit хук
   printf 'TOKEN=s3cret\n' > .env && git add .env && git show :.env   # у git — зашифрований файл
   ```
   Для `dotnet run` без опублікованого бінарника підійде обгортка `forest` у `PATH`: `exec dotnet <репозиторій>/src/Cli/bin/Debug/net10.0/forest.dll "$@"`.

   Підписані коміти (ADR-0011): ЦА потрібен `ssh-keygen` (пакет `openssh-client`) у `PATH`. Ключ підпису додає сама людина (сесія, не API-токен), стек вмикає вимогу, а непідписаний коміт можна підтвердити за SHA:
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/me/signing-keys -H 'content-type: application/json' -d "{\"name\":\"laptop\",\"publicKey\":\"$(cat ~/.ssh/id_ed25519.pub)\",\"environmentIds\":null}"
   curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/require-signed-commits -H 'content-type: application/json' -d '{"enabled":true}'
   git config gpg.format ssh && git config user.signingkey ~/.ssh/id_ed25519.pub && git commit -S -m '…'
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/stacks/<stackId>/deployments -H 'content-type: application/json' -d '{"pull":false,"approveCommit":"<повний SHA>"}'   # лише для непідписаного коміту
   ```

   Політика compose й адміністратор середовища (ADR-0012): адміністратор призначає роль `EnvironmentAdmin` на середовище, той змінює політику й винятки. Для перевірки рендерингу агенту потрібен `docker compose`; для шляхів поза `/opt/container-forest` — корінь хоста (`CF_AGENT__HOSTROOT`, у контейнері агента `/:/host:ro,rslave`):
   ```sh
   curl -X POST -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/role-assignments -H 'content-type: application/json' -d '{"userName":"alice","stackId":null,"role":"EnvironmentAdmin"}'
   curl -X PUT -H 'X-Dev-User: alice' http://localhost:5080/api/environments/<id>/compose-policy -H 'content-type: application/json' -d '{"mode":"Audit","dataRoot":"/var/lib/container-forest/data","systemReadOnlyPaths":["/etc/localtime"],"allowances":["network:proxy"]}'
   curl -X PUT -H 'X-Dev-User: alice' http://localhost:5080/api/stacks/<stackId>/policy-allowances -H 'content-type: application/json' -d '{"allowances":["docker-socket"]}'
   curl -H 'X-Dev-User: alice' http://localhost:5080/api/environments/<id>/compose-policy/report   # розгорнуті стеки за поточною політикою
   curl -X POST -H 'X-Dev-User: alice' 'http://localhost:5080/api/environments/<id>/age-keys?stackId=<stackId>'   # ключ SOPS лише цього стеку
   ```

   Образи з приватного registry: креди задаються на середовищі (лише адміністратор; пароль ніколи не повертається). Повторний `PUT` для того самого `server` замінює логін:
   ```sh
   curl -X PUT -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials -H 'content-type: application/json' -d '{"server":"ghcr.io","username":"<user>","password":"<token>"}'
   curl -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials
   curl -X DELETE -H 'X-Dev-User: root' http://localhost:5080/api/environments/<id>/registry-credentials/<credentialId>
   ```
   Паролі шифруються ключами з `data-protection-keys/` у робочому каталозі ЦА (`CF_DATAPROTECTION__KEYDIRECTORY`): без цього каталогу збережені креди не розшифруються.

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
5. Панель: `cd src/Web && npm ci`, потім `CF_DEV_USER=root npm run dev` (PowerShell: `$env:CF_DEV_USER="root"; npm run dev`) — `http://localhost:5173`. Dev-сервер проксює `/api` і `/hubs` на ЦА (`CF_AGGREGATOR_URL`, типово `http://localhost:5080`) і, якщо задано `CF_DEV_USER`, додає до кожного запиту `X-Dev-User` з неї. Без неї панель відправить на `/login`, де можна ввійти паролем, наприклад адміністратором із `CF_BOOTSTRAP__*` (крок 1).
   - Змінили DTO в ЦА — зберіть `src/Aggregator/Api` (оновиться OpenAPI-документ) і виконайте `npm run generate:api`: типи клієнта в `src/api/schema.gen.ts` генеруються з нього.
   - PWA (service worker, встановлення, робота без мережі) є лише в продакшн-збірці: `npm run build`, потім `CF_DEV_USER=root npm run preview` — `http://localhost:4173` з тим самим проксі на ЦА. Змінили логотип — `scripts/pwa-icons.sh` перерендерить іконки (потрібен Google Chrome).
   - `src/routeTree.gen.ts` генерує плагін TanStack Router під час `npm run dev`/`build`; обидва згенеровані файли комітяться й руками не редагуються.
   - Текст, який бачить користувач, не пишіть у розмітці: додайте ключ у `src/i18n/locales/uk.ts` і переклад у `en.ts`, а в компоненті — `const { t } = useTranslation()` і `t("screen.key")`. Пропущений переклад або неіснуючий ключ — помилка `npm run typecheck`.
   - Компоненти UI — shadcn/ui: `npm run ui:add -- table` (з `src/Web`) додає компонент у `src/components/ui` і переводить його іконки на Material Symbols; згенеровані файли комітяться. Якщо після цього `npm run typecheck` скаржиться на відсутню іконку, додайте її в `src/components/icons.ts` (назви — як у Lucide, іконки — з https://fonts.google.com/icons).
   - Docker-образ панелі (Caddy: статичні файли, проксі `/api`, `/hubs` і gRPC на ЦА, TLS — ADR-0005): `docker build -f src/Web/Dockerfile -t container-forest-web:dev .` (контекст — корінь репозиторію: панель вбудовує `deploy/agent/compose.yaml`).

   Вхід і прив'язка через Discord (ADR-0009) потребують тестового застосунку Discord з redirect `http://localhost:5080/api/auth/discord/callback` і `CF_DISCORDAUTH__CLIENTID` / `CF_DISCORDAUTH__CLIENTSECRET` у ЦА. Проксі Vite переписує `Host`, тож Discord повертає браузер прямо на ЦА (`localhost:5080`), а не на панель: після входу відкрийте `http://localhost:5173` самі.
6. CLI `forest` (ADR-0008): `dotnet run --project src/Cli -- login http://localhost:5173`. Адреса — панелі (dev-сервер Vite з кроку 5): CLI відкриває в браузері її сторінку підтвердження, а API Vite проксює на ЦА. Конфігурацію з токеном CLI пише в `$XDG_CONFIG_HOME/container-forest/config.json`; для окремої тестової конфігурації задайте `XDG_CONFIG_HOME`. Без браузера: `--device` (код вводиться на `/cli/device`) або токен з екрана «API-токени»: `CF_URL=http://localhost:5173 CF_TOKEN=cf_pat_… dotnet run --project src/Cli -- env list`.
   - Змінили API ЦА — після збірки `src/Aggregator/Api` перегенеруйте клієнт: `dotnet tool restore && scripts/generate-cli-client.sh`, згенеровані файли закомітьте (CI перевіряє).
   - Один бінарник без рантайму (NativeAOT, потрібен clang): `dotnet publish src/Cli -r linux-x64 -c Release -o out` → `out/forest`.
   - Реліз: `git tag cli-v0.2.0 && git push origin cli-v0.2.0` — workflow `CLI` збирає бінарники для linux x64/arm64, macOS arm64 і Windows x64 і публікує їх у GitHub Releases разом із `SHA256SUMS`.

Тести: `dotnet test --solution ContainerForest.slnx`. Тести зі справжніми `sops` і `age-keygen` без них у `PATH` пропускаються (у CI — обов'язкові, `CF_TEST_REQUIRE_SOPS=1`), так само тести підписів зі справжнім `ssh-keygen` (`CF_TEST_REQUIRE_SSH_KEYGEN=1`) і тести рендерингу агента зі справжнім `docker compose` (`CF_TEST_REQUIRE_DOCKER_COMPOSE=1`; демона Docker вони не потребують). Інтеграційні тести в `tests/Aggregator/Api` піднімають ЦА в пам'яті й підключаються до нього як агент. Тести в `tests/DiscordBot` піднімають у пам'яті gRPC-сервер `BotGateway` із заготовленими відповідями.

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

Розгортання ЦА з панеллю (`deploy/aggregator/compose.yaml`, опис — у [docs/architecture.md](docs/architecture.md#розгортання-ца)) можна перевірити локально без домену й TLS: зберіть образи ЦА й панелі, у `.env` задайте `CF_DOMAIN=:8080` і перший адміністратор, а в `compose.override.yaml` — локальні образи й порт `8080:8080` замість 80 і 443. Агент тоді підключається до `http://<хост>:8080`: Caddy пропускає gRPC і без TLS (h2c).

```sh
docker build -f src/Aggregator/Api/Dockerfile -t container-forest-aggregator:dev .
docker build -f src/Web/Dockerfile -t container-forest-web:dev .
```

Образ Discord-бота збирається так само; приклад розгортання — [`deploy/discord-bot/compose.yaml`](deploy/discord-bot/compose.yaml) (опис — у [docs/architecture.md](docs/architecture.md#розгортання-discord-бота)). Локально токени зручно передати файлом `.env.local` з рядками `CF_DISCORD__TOKEN=…` і `CF_BOT__SERVICETOKEN=…` (він у `.gitignore`), а в `CF_BOT__CONTROLLERURL` вказати `http://host.docker.internal:5081`. Образ не читає user-secrets і стартує в оточенні `Production`, тож команди реєструються глобально; для тестового сервера додайте `-e DOTNET_ENVIRONMENT=Development -e CF_DISCORD__DEVGUILDID=<id сервера>`:

```sh
docker build -f src/DiscordBot/Dockerfile -t container-forest-discord-bot:dev .
docker run -d --name cf-discord-bot --env-file .env.local \
  -e CF_BOT__CONTROLLERURL=http://host.docker.internal:5081 \
  container-forest-discord-bot:dev
```

Сайт документації (DocFX): документи з `docs/` і довідник API з XML-коментарів у `src/`. Згенерований код (protobuf, міграції) у довідник не входить (`docs/api-filter.yml`). Workflow `Docs` збирає сайт на кожен PR і публікує його з `main` на GitHub Pages. Локально:

```sh
dotnet tool restore
dotnet docfx docfx.json --serve   # http://localhost:8080
```

Коментарі `///` пишіть для public-типів і членів: з них складається довідник.

Міграції EF Core (інструмент ставиться з `dotnet-tools.json`):

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Aggregator/Infrastructure --output-dir Persistence/Migrations
```

⚠️ Для розробки використовуйте локальний Docker або тестовий сервер, **не продакшн-інфраструктуру**.

## CI

GitHub Actions (`.github/workflows/ci.yml`) запускається на кожен PR і на пуш у `main`. PR мержиться лише із зеленим CI. Кроки:

1. `dotnet restore` і `dotnet build` з `CI=true`: попередження компілятора й аналізаторів стають помилками (`Directory.Build.props`).
2. Перевірка, що закомічений `src/Aggregator/Api/openapi/*.json` збігається з тим, що згенерувала збірка. Якщо крок упав — зберіть `src/Aggregator/Api` локально й закомітьте JSON. Так само для клієнта CLI: `scripts/generate-cli-client.sh` не має нічого змінити в `src/Cli/Api/Generated`.
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
