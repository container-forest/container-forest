# Некеровані стеки

Compose-проєкти, які адміністратор хоста запускає вручну. Рішення й обґрунтування — в [ADR-0016](../adr/0016-unmanaged-stacks.md). Шляхи ЦА відносно `src/Aggregator`, агента — `src/Agent`, панелі — `src/Web`.

## Налаштування на хості

У `deploy/agent/compose.yaml` агента додаються корінь і його монтування за тим самим шляхом (достатньо лише для читання):

```yaml
    environment:
      CF_AGENT__UNMANAGEDSTACKROOTS__0: /srv/docker
    volumes:
      - /srv/docker:/srv/docker:ro
```

Корінь, якого в контейнері агента немає, агент пропускає з попередженням у лозі. Корінь, що перетинається з `DataDirectory`, `StacksDirectory` чи `SecretsDirectory`, — помилка налаштувань на старті.

## Агент

- **Пошук** — `UnmanagedStackFinder`. Стеки за мітками бере з `IDockerGateway.ListComposeContainersAsync` (фільтр `label=com.docker.compose.project`, мітки `working_dir`, `config_files`, `environment_file` — `ContainerInfoMapper.ToComposeContainer`), стеки з каталогів дає `Scan`. Якщо імена збігаються, перемагає стек із контейнерами, а каталог, що вже належить проєкту з контейнерами, сканування пропускає. Ліміти: глибина `MaxScanDepth` (3), не більше `MaxScannedDirectories` (2000) каталогів на корінь, `.env` і compose-файл для імені — до 1 МБ.
- **Ім'я** — `UnmanagedStackFinder.ProjectNameOf`. Щоб ім'я, знайдене скануванням, збігалося з тим, яке дасть compose, `-p` агент передає завжди.
- **Команди** — `ListUnmanagedStacksHandler` і `ControlUnmanagedStackHandler` (`Commands/UnmanagedStackHandlers.cs`), блокування — `StackLocks`, ті самі, що в керованих стеків. Аргументи будує `ComposeInvocation.UnmanagedUp` і `UnmanagedPull` (`-p`, `--project-directory`, `-f` для кожного файлу, `--env-file`, робочий каталог — каталог проєкту). Для restart і down — ті самі `ComposeInvocation.Restart` і `Down`, що й для керованих стеків: без `-f`, у `StacksDirectory`.
- **Видалення контейнера** — `RemoveContainerHandler` → `DockerDotNetGateway.RemoveContainerAsync` (`Force = false`, `RemoveVolumes = false`). Запущений контейнер Docker не видаляє, і агент повертає його помилку як `ERROR_CODE_COMMAND_FAILED`.

## ЦА

- **Сервіс** — `Application/Stacks/UnmanagedStackService`. `ListAsync` для користувача без ролі на всьому середовищі повертає порожній список і агента не питає. `ExecuteAsync` перевіряє все в такому порядку:
  1. середовище видно — інакше 404;
  2. є роль на середовищі, а ім'я коректне — інакше 404;
  3. ім'я не належить керованому стеку, а агент такий стек знайшов — інакше 404;
  4. стек не системний — інакше 409;
  5. роль достатня: restart — Operator, решта — Editor, інакше 403;
  6. креди registry для pull і up;
  7. агент.

  Тайм-аут зупинки й вивід (останні 1000 рядків) — як у `StackCommand`. Запит до агента йде без токена викликача.
- **Аудит:** `UnmanagedStackStarted`, `UnmanagedStackPulled`, `UnmanagedStackRestarted`, `UnmanagedStackTakenDown` (категорія Stacks, ціль `StackAuditTarget` без id), `ContainerRemoved` (Containers).
- **REST** — `Api/Endpoints/EnvironmentEndpoints.cs`, перелік — коментар у `Api/Contracts/EnvironmentContracts.cs`. `ContainerResponse.canRemove` — право Deploy, для системного контейнера завжди false.
- **Видалення контейнера** — `RemoveContainerCommand` у `ContainerCommandService`. Через `AuthorizedCommand` воно потребує `Permission.Deploy` (`IContainerCommand.RequiredPermission` тепер перевизначається), а через `IsAllowedIn` — стану created, exited або dead.

## Бот

`ListStacks` додає некеровані стеки до керованих (`unmanaged`, `can_up`, `can_pull`; `id` порожній). Помилку агента, зокрема `Unsupported` старого агента, ЦА ковтає: керовані стеки видно й при офлайн-агенті. `ControlStack` спершу шукає видимий керований стек, інакше звертається до `UnmanagedStackService`. Up і pull керованого стеку — `FAILED_PRECONDITION`. Команди: `/stack-up`, `/stack-pull` (лише некеровані), `/stack-restart`, `/stack-down` (обидва види), `/remove` (контейнер). Автодоповнення пропонує некеровані стеки разом із розгорнутими.

## CLI

`forest unmanaged list|up|pull|restart|down <стек> [-e <середовище>]`; down вимагає `--yes`, restart і down мають `-t`. `forest container remove <контейнер> --yes`.

## Панель

Розділ «Некеровані стеки» під таблицею стеків на екрані `/stacks` (`components/stacks/unmanaged-stacks-section.tsx`, правила — `features/unmanaged-stacks/model.ts`). Розділ не показується, коли агент офлайн, коли некерованих стеків немає або коли в користувача немає ролі на середовищі. Рядок відкриває сторінку стеку, кнопки в рядку — швидкі дії. Restart і down просять підтвердження, а вивід compose розгортається під повідомленням про результат. Restart і down вимкнено для стеку без контейнерів. Подія `ContainersChanged` інвалідовує й список некерованих стеків. Кнопка видалення контейнера в `ContainerTable` з'являється, лише коли `canRemove`, і діє лише для зупиненого контейнера; підтвердження — без тайм-ауту зупинки.

Сторінка стеку — `/stacks/unmanaged/$environmentId/$stackName` (`routes/_authed/stacks/unmanaged.$environmentId.$stackName.tsx`). Окремого ендпоінта в ЦА немає: сторінка бере стек зі списку `unmanagedStacksQuery` за ім'ям, тож стек, якого агент більше не знаходить, показується як «не знайдено». Картки:

- **Дії** — up, pull, restart, down тими самими правилами, що й у розділі; для системного стеку замість кнопок — пояснення.
- **Контейнери** — `StackContainersCard` (`components/stacks/stack-containers-card.tsx`), спільна з керованим стеком: контейнери середовища з таким `stackName`.
- **Файли** — каталог проєкту, compose-файли і як стек знайдено (за мітками чи скануванням каталогу).
- **Останні дії** — лише для адміністраторів, бо журнал аудиту їхній. Запит — `auditQuery` за середовищем, категорією Stacks і пошуком за назвою. Пошук журналу шукає підрядок, тож `isUnmanagedStackEntry` лишає записи з точно цією назвою, без `stackId` і з діями `UnmanagedStack*`. Показує п'ять останніх і посилання на журнал із тими самими фільтрами. Мутація дії інвалідовує й `["audit"]`. У журналі аудиту такий запис веде на сторінку стеку (`targetLink`).
