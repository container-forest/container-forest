# Оновлення образів самою ЦА

Правила й обґрунтування — в [ADR-0015](../adr/0015-image-updates.md). Тут — де це в коді й деталі, яких там немає. Шляхи відносно `src/Aggregator`.

## Модель

- **Код:** `Domain/Images/` (`ImageName`, `ImagePolicy`, `ImageUpdateRecord`, `ImageTagCacheEntry`, `AggregatorSigningKey`), `Application/Images/`, `Infrastructure/Images/`, контракти — `Api/Contracts/ImageUpdateContracts.cs`.
- **`ImageName`** нормалізує ім'я так, як його резолвить Docker: `nginx` → `docker.io/library/nginx`, хост — через `RegistryHost`. Політика й сервіс compose збігаються лише за рівністю `ImageName`, не за підрядком. Ім'я зі змінними (`$`) не є `ImageName`.
- **`ImagePolicy`** — окрема сутність зі `StackId` (каскад зі стеком), до `ImagePolicy.MaxPerStack` на стек, одна на образ (унікальний індекс). Шаблон тегу компілюється з тайм-аутом, має починатися з `^` і закінчуватися на `\z`; для `ImageTagOrder.TagTimestamp` потрібна група `ts`. Мітки часу порівнюються спершу за довжиною, потім як текст (`ImagePolicy.CompareTimestamps`), тож працюють і числа без нулів спереду, і формат `20261109T120000`.
  - Стан останньої перевірки — `LastCheckedAt`, `LastError`, `LatestTag`, `Warning`. `RecordFailed` каже, чи сповіщати: перша помилка після успішної перевірки, а недоступний registry — лише з третьої поспіль (`RegistryFailures`).
  - `LastChangedByAdministrator` — чи останню зміну зробив адміністратор системи або середовища (п. 7 ADR).
  - Поля `Mode` з ADR немає: поки є лише режим коміту, а поле з'явиться разом із режимом PR.
- **`ImageUpdateRecord`** — запис коміту оновлення: `Commit`, `Parent`, `ComposePath`, зміни (`ImageChange`: сервіс, було, стало; JSON-колонка), причина (`ImageUpdateReason`), хто попросив. Ідентифікатор відомий до коміту, бо його називає трейлер `Forest-Image-Update`. Унікальний за (стек, коміт), каскад зі стеком; зовнішнього ключа на політику немає: її можуть видалити, поки її коміти ще розгорнуті.
- **`ImageTagCacheEntry`** — кеш тегів (`ImageTagCache`, ключ — образ і тег): digest і `created`, спільний для всіх політик образу, переживає рестарт.
- **Хук образів** — поля стеку `ImageHookTokenHash` і `ImageHookAllowsDowngrade`, як у хука деплою (`SecretToken`).
- **Токен запису** — `GitConnection.ProtectedPushToken` і `PushUserName` (`SecretPurpose.GitPushToken`). Зміна префікса на інший хост із будь-яким токеном — 400, як для токена доступу.

## Перевірка й коміт

`ImageUpdateService.CheckAsync` під замком стеку (`ImageUpdateLocks`; той самий замок бере зміна й видалення політики):

1. Закріплену політику опитування й хук не чіпають (`Pinned`).
2. Fetch голови гілки (`GitAccessResolver.ForFetch`), compose-файл стеку з неї, слоти образу — `IComposeImageRewriter.Find`.
3. Ціль:
   - без тегу — `ImageTagSelector`: кандидати за шаблоном; для `TagTimestamp` — найбільша мітка, маніфест лише її (digest); для `ImageCreated` — `created` з кешу, невідомі дочитуються до `ImageUpdateOptions.MaxTagReadsPerCheck` з кінця списку тегів. Поки прочитано не все — `Waiting` (`CatalogIncomplete`). Різні образи з однаковим найновішим `created` — помилка політики, а не вибір навмання. Понад `ImageTagSelector.ManyCandidates` кандидатів або 429 — попередження на політиці;
   - з тегом — шаблон, наявність у registry, напрям за порядком політики. Старіший від хука без дозволу — 409. Запит людини або відкат закріплює образ.
4. Нічого міняти — `UpToDate`; dry run — `WouldUpdate` без запису (кеш тегів однаково оновлюється).
5. Агент офлайн — `AgentOfflineException` (інваріант 7). Токен запису — `GitAccessResolver.ForPush`, ніколи не токен читання.
6. Якщо стек вимагає підписів, голова має бути засвідчена (`CommitEndorsementService` або підтвердження за SHA) і бути нащадком `DeployedCommit`.
7. `IComposeImageRewriter.Rewrite`, коміт `IGitWriter.CreateCommitAsync` з підписом `ICommitSigner`, запис оновлення в базу **до** push (webhook від власного push може прийти раніше, ніж push поверне керування), `IGitWriter.PushAsync`. `GitPushRejectedException` — fetch, перевірка й зміна заново, до `ImageUpdateService.MaxPushAttempts` разів.
8. Деплой — як у хука деплою, під `PushDeploymentGate`: деплой у черзі без закріплення або той, що вже йде з цим комітом, бере його сам; інший незавершений — позначка для `PushFollowUpObserver`; інакше `Deployment.QueueFromImageUpdate` — закріплений коміт, `pull: missing`, тригер `ImageUpdate`.

Помилки registry, git і compose-файлу не кидаються далі: політика записує їх (`RecordFailed`), сповіщення — `ImageUpdateFailed`, результат — `Failed` з причиною.

- **Переписувач** — `YamlComposeImageRewriter`: позиції скалярів YamlDotNet (`Mark.Index`, символи), заміна з кінця, стиль лапок зберігається, після заміни файл читається знову й порівнюються всі образи (`Verify`). Відмова для значень з якорем або аліасом (значення, що стоїть раніше за свій ключ `image`, — аліас: якір завжди раніше), з `<<`, блокового або з тегом YAML, зі змінними. `Apply` застосовує записані зміни для перевірки коміту.
- **Git** — `LibGit2SharpGitSource` реалізує й `IGitWriter`: дерево з одним новим blob (режим файлу зберігається), `Commit.CreateBuffer`, підпис поза замком клону, `ObjectDatabase.CreateCommitWithSignature`; push через тимчасове посилання `refs/forest/image-update` без `+`, після нього трекінг-гілка клону стає на новий коміт. `NonFastForwardException` і відповіді «fetch first» / «non-fast-forward» — `GitPushRejectedException`, решта — `DeploymentFailedException`.
- **Ключ ЦА** — `AggregatorSigningKeyService`: створюється під час першого оновлення (`SshKeygenKeyGenerator`, `ssh-keygen -t ed25519`), приватна частина — `SecretPurpose.AggregatorSigningKey`. Підпис — `SshKeygenCommitSigner` (`ssh-keygen -Y sign -n git`, ключ у тимчасовому каталозі 0700 і файлі 0600). Ротація залишає старі ключі з `RetiredAt`. `SigningKeyService` відхиляє відбиток будь-якого з них.
- **Registry** — `OciRegistryClient` на власному `HttpClient` (без фабрики HTTP-клієнтів: вона потребувала б нового пакета). Анонімно → на 401 токен Bearer з realm (логін середовища йде туди лише на хост самого registry або `auth.docker.io` для Docker Hub) або Basic. Переадресації вручну, `Authorization` лише на початковий хост. `ConnectCallback` перевіряє адреси під час з'єднання й під'єднується саме до перевірених (`GitHostGuard.IsPublic`), тож DNS rebinding не обходить заборону приватних адрес; `ImageUpdateOptions.AllowPrivateRegistries` її знімає. Для індексу `created` береться з першої платформи `linux`, не `unknown`.

## Засвідчення

`CommitEndorsementService` винесено з `VerifyCommitStep`: перевірка підпису ADR-0011 і ланцюжок оновлень.

- Підпис не дав засвідчення з причиною `Unsigned` або `UnknownKey` (ключ ЦА невідомий навмисно) — шукається запис за (стек, коміт). Далі для кожного запису в ланцюжку, до `CommitEndorsementService.MaxUpdateChain`: `ICommitInspector.DiffAsync` — єдиний батько дорівнює `Parent`, змінено лише `ComposePath` без зміни режиму, а `IComposeImageRewriter.Apply` до батьківського файлу дає рівно новий. Батько підписаний — засвідчено, `SignedBy` — підписант кореня; підтверджений за SHA (`IDeploymentRepository.WasApprovedAsync`) — засвідчено без підписанта; сам має запис — крок далі; інакше `ImageUpdateMismatch`.
- `CommitEndorsement.ImageUpdate`, `Deployment.ImageUpdateId`, у відповідях — `EndorsementResponse`.
- **Звільнення від політики compose** (`ComposePolicyStep`): для `ImageUpdate` підписант кореня має адмініструвати середовище, а кожна політика ланцюжка — `LastChangedByAdministrator`. Видалена політика звільнення не дає.

## Запуск

- **Опитування** — `ImageUpdatePoller` (раз на хвилину з `ImageUpdateScheduler`): секція налаштувань `ImageUpdateSettingsSection` (вмикання, інтервал 1–1440 хв, типово 5), політики з `Poll` без закріплення, стеки з онлайн-агентом, кожна перевірка у власному scope.
- **Хук** — `ImageHookService`, `POST /api/image-hooks/{stackId}`: однаковий 401 (`WebhookRejectedException`), політики з `AcceptRequests` (усі або за `image`), 404 (`ImageHookNotFoundException`), тег без образу — 400.
- **Люди** — `ImagePolicyService`: Editor стеку додає, змінює, видаляє, перевіряє, закріплює й відкріплює; dry run — будь-хто, хто бачить стек. Під час збереження політика перевіряється на голові гілки й вимагає токен запису на підключенні.
- **Аудит:** `ImagePolicyAdded/Changed/Removed`, `ImagePinned/Unpinned`, `ImageHookCreated/Removed`, `GitPushCredentialChanged`, `AggregatorSigningKeyRotated`; перевірка людиною — `DeploymentQueued` з деталями.
- **CLI:** `forest stack image-policy list|add|change|remove|check|pin|unpin`, `forest stack image-hook create|downgrade|remove`, `forest connection set-push-token|remove-push-token`, `forest system signing-key show|rotate`. Налаштування опитування — лише панель (`/api/settings/**` у CLI немає).

## Тести

- Домен — `ImagePolicyTests`; вибір тегу — `ImageTagSelectorTests`; ланцюжок засвідчення — `VerifyCommitStepTests`; звільнення — `ComposePolicyStepTests`.
- Інфраструктура: `YamlComposeImageRewriterTests`, `OciRegistryClientTests` (фейковий HTTP), `GitWriterTests` — локальний bare-репозиторій і справжній `ssh-keygen` (без нього пропускаються, у CI падають).
- API: `ImageUpdateTests` — registry, запис у git і підпис підмінені; коміт, push і деплой із тригером `ImageUpdate` проходять крізь справжній пайплайн.
