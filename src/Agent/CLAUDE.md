# Агент: конвенції

Обробники команд, робота з Docker і compose. Що агент отримує від ЦА: [деплой](../../docs/design/deployment.md), [секрети](../../docs/design/secrets.md), [політика compose](../../docs/design/compose-policy.md), [контейнери й логи](../../docs/design/containers.md).

## Контракт

`src/Contracts/agent/agent.proto` — єдине джерело; копію сюди не переносити. Покриває обсяг альфа-версії. Відкладено: статистика для графіків, exec/attach, окрема команда rehydrate (поки що — повторний `DeployStack`).

## З'єднання й команди

- Агент: перепідключення з експоненційною затримкою (до 60 с) і jitter; якщо сесія жила довше хвилини, лічильник спроб скидається. Heartbeat кожні 15 с.
- На агенті команди диспетчеризуються за `ControllerMessage.BodyCase` до `IAgentCommandHandler`; обробники отримують `ICommandProgress` для стрімінгу виводу.
- Кожна команда, крім `CancelRequest`, отримує рівно один `CommandResult` з тим самим `request_id`, інакше ЦА чекатиме до таймауту. Команда без обробника (зокрема невідомий агенту варіант `oneof`, що парситься як `BodyCase.None`) — `ERROR_CODE_UNSUPPORTED`. `CommandError.message` показується в панелі: читабельний текст без деталей винятків; деталі — у лог агента.
- Скасування: `CommandDispatcher` тримає `RunningCommands` (request_id → токен, пов'язаний із токеном сесії). `CancelRequest` скасовує токен команди, обробник завершується `ERROR_CODE_CANCELLED` (compose вбивається, виклик Docker API обривається); невідомий id ігнорується, дублікат request_id запущеної команди — `ERROR_CODE_INVALID_ARGUMENT`. Команда реєструється синхронно до першого `await`, а сесія читає наступне повідомлення лише після цього, тож `CancelRequest` не випереджає свою ціль. Скасований деплой не видаляє `.prev` секретів.

## Docker

- Агент використовує форк `Docker.DotNet.Enhanced` (Testcontainers), не закинутий оригінал. Клієнт — `new DockerClientBuilder().Build()`: endpoint як у docker CLI (`DOCKER_HOST` → поточний контекст → типовий сокет). Власний таймаут клієнта (100 с) вимкнено, бо ЦА дозволяє час зупинки до 5 хв; кожен виклик має свій бюджет у `DockerDotNetGateway`.
- `timeout_seconds = 0` у Stop/Restart означає «вбити одразу» (ЦА приймає 0), а не типовий час Docker.
- `container_id` перед зверненням до Docker API перевіряється регулярним виразом `^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z`: id стає частиною шляху запиту.
- Облікові дані агента — `<DataDirectory>/agent-credentials.json`, запис атомарний, на Unix з правами 0600 від створення. Hostname для `Enroll` — `Environment.MachineName`; в контейнері його задає `hostname:` у compose агента (окремої змінної середовища немає).
- Образ агента — `src/Agent/Dockerfile`, контекст — корінь репозиторію. База — `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04, не Debian): у неї ставляться `docker-ce-cli` і `docker-compose-plugin` з apt-репозиторію Docker, дистрибутив береться з `ID` в `/etc/os-release`. `WORKDIR /app` обов'язковий: без нього content root — `/` і `appsettings.json` не читається. Агент працює від root, бо gid сокета Docker різний на різних хостах.

## Compose і стеки

- `docker compose` запускати через `ProcessStartInfo.ArgumentList` (без shell). Env-секрети передавати в `psi.Environment`, не в `.env`-файл.
- Операції зі стеками: `restart` = `docker compose restart`; `redeploy` = `docker compose -p <stack> -f compose.yaml up -d --remove-orphans --pull missing --wait`; `redeploy with pull` = те саме з `--pull always`. Агент додає глобальні `--ansi never --progress plain`, щоб кожен рядок виводу йшов у лог деплою текстом. Аргументи будує лише `ComposeInvocation` (фабрики `Up`/`Restart`/`Down`); процес запускає `DockerComposeCli` без shell, з тайм-аутом `Agent:ComposeTimeout` (15 хв) і вбиває дерево процесів при скасуванні. Змінні `CF_*` агента в процес compose не передаються: compose-файл міг би підставити їх у контейнер.
- Одна операція на стек за раз (`StackLocks`): друга отримує `ERROR_CODE_BUSY`, а не чекає в черзі. `RestartStack` і `DownStack` для стеку без каталогу — `ERROR_CODE_NOT_FOUND` (compose для неіснуючого проєкту мовчки повертає 0).
- У пакеті `DeployStack` compose-файл стеку завжди називається `compose.yaml` і лежить у корені каталогу стеку, хоч як би він звався в репозиторії (`StackBundle.ComposeFileName`). Агент передає його через `-f compose.yaml`, тож інші compose-файли в каталозі (зокрема `compose.override.yaml`) не підмішуються.
- `RestartStack` і `DownStack` виконувати як `docker compose -p <stack> restart|down --timeout <N>` **без `-f`**, з робочим каталогом `StacksDirectory`, а не каталогом стеку. Так compose не читає файл і не вимагає env-змінних, яких на диску немає; з `-f` або без `-p` у каталозі стеку команда падає на обов'язкових змінних `${VAR:?}` (перевірено на Compose 5.5.1). Env лишається в конфігурації контейнерів.
- Некеровані стеки (ADR-0016, [опис](../../docs/design/unmanaged-stacks.md)) агент знаходить сам у `Agent:UnmanagedStackRoots` (`UnmanagedStackFinder`), а ЦА називає їх лише іменем. Шлях від ЦА агент для них ніколи не приймає. Up і pull запускаються з `-p`, `--project-directory`, `-f` і `--env-file` з міток контейнерів, restart і down — так само, як для керованих стеків.

## Файли стеку й секрети

- Каталог стеку не замінюється (ADR-0004): контейнери монтують шляхи в ньому, і заміна каталогу лишила б їх на старій копії з даними. `StackFileStore` готує кожен файл у `<name>.tmp` і перейменовує поверх цільового; файли попереднього пакета, яких немає в новому, видаляються за маніфестом `<StacksDirectory>/<name>.manifest.json` (лежить поза каталогом стеку, щоб контейнер не міг його переписати). Файли, створені контейнерами, не чіпаються. Попередня версія пакета — копія в `<name>.prev`.
- Агент працює від root, а контейнер може створити в змонтованому каталозі посилання на `/`: перед записом, копіюванням і видаленням перевіряється, що між каталогом стеку й файлом немає символьних посилань. Каталог на хості там, де в пакеті файл, — `StackFileConflictException` (`ERROR_CODE_COMMAND_FAILED`) до будь-яких змін.
- Файли з `StackFile.secret = true` пишуться в `/run/container-forest/<stack>/<path>` (каталог стеку 0700; замінюється цілим каталогом `.tmp` → поточний → `.prev`, бо контейнери туди не пишуть), а в каталозі стеку за тим самим відносним шляхом лежить символьне посилання на них. Compose-файл у репозиторії посилається на секрет звичайним відносним шляхом (`file: ./secrets/db_password`), а Docker розв'язує посилання на хості: шляхи на хості й в агенті однакові. Вміст секретів на диск не потрапляє; після перезавантаження хоста посилання порожні до rehydrate. `.prev` із секретами видаляється одразу після успішного `compose up`, після `DownStack` — секрети стеку. На Windows (лише розробка) секретні файли можна писати прямо в каталог стеку з попередженням у лозі.

## Пастки

- Розробка на Windows: Docker.DotNet підключається через named pipe поточного Docker-контексту (`npipe://./pipe/dockerDesktopLinuxEngine` у Docker Desktop). Якщо Docker вимкнено, підключення чекає 10 с і завершується `TimeoutException`; `/proc/.../boot_id` немає, тому boot_id — час старту системи (`UptimeBootIdProvider`).
