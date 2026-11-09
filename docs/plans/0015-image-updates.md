# План реалізації ADR-0015: оновлення образів самою ЦА

Рішення й обґрунтування — в [ADR-0015](../adr/0015-image-updates.md). Цей план фіксує, **як** його реалізувати, етап за етапом. Після злиття PR #128 план видаляється, а опис підсистеми лишається в `docs/design/image-updates.md` (етап 7).

## Правила для кожної сесії

- Прочитати ADR-0015 повністю, кореневий `CLAUDE.md`, `src/Aggregator/CLAUDE.md` і документи з таблиці «Де що шукати» для частин, яких торкається етап. Етап — лише свій: не забігати вперед і не переробляти попередні без потреби.
- Гілка `docs/adr-0015-image-updates`, PR #128. Коміти за Conventional Commits; кожен етап — один або кілька комітів, після кожного CI зелений: тести, попередження як помилки, актуальні OpenAPI-документ і клієнти (див. [CI](../../CONTRIBUTING.md#ci)).
- **Облік:** усі класи, методи й екрани — Петренко Б. О. (@lepterion). Не питати.
- **Сумісність із продакшном:** міграції EF Core лише додають таблиці й колонки з типовими значеннями; `.proto` — лише нові значення enum; нові налаштування — з типовими значеннями. Стек без політики образів поводиться рівно як зараз.
- **Залежності:** нових не додавати. YamlDotNet і LibGit2Sharp уже є в ЦА, клієнт OCI — на `HttpClient`. Якщо все ж знадобиться — зупинитися й спитати.
- Інваріанти безпеки (`docs/security-invariants.md`) не порушувати; токени — лише хеші (інваріант 8), секрети — через `ISecretProtector` (ADR-0003).
- Тести — xUnit v3, структура `tests/` повторює `src/`. Тести зі справжнім `ssh-keygen` пропускаються без нього, а в CI (`CF_TEST_REQUIRE_SSH_KEYGEN=1`) падають, як `LibGit2SharpGitSourceTests`.
- Наприкінці етапу — коротко записати в розділ «Журнал» цього файлу, що зроблено й що відхилилося від плану.

## Рішення реалізації, спільні для етапів

Те, чого ADR не фіксує, але що має бути однаковим у всіх сесіях.

- **Домен:** простір імен `Domain/Images`: `ImagePolicy`, `ImageUpdateRecord`, `ImageTagCacheEntry`, `AggregatorSigningKey`; ідентифікатори — строго типізовані, як наявні (`StackId`, `SigningKeyId`), сутності — від `Entity<TId>`.
- **Нормалізація образу** — наявні `ImageReference` і `RegistryHost` (`Domain/Deployments`). Нового парсера не писати.
- **`ImagePolicy`** — окрема сутність з `StackId` (каскад зі стеком), не частина `Stack`. Поля з ADR п. 1 плюс `LastChangedBy`, `LastChangedByAdministrator` (для п. 7), `CatalogComplete` (перша перевірка `ImageCreated` прочитала всі теги), `LastCheckedAt`, `LastError`, `FailureNotified`.
- **Хук образів** — поля на `Stack` за зразком хука деплою: `ImageHookTokenHash`, `ImageHookAllowsDowngrade`; генерація й хешування — `SecretToken`.
- **`PushCredential`** — лише на `GitConnection` (`ProtectedPushToken`, `PushUserName`), за зразком `ProtectedAccessToken` / `AccessUserName`. Репозиторій без підключення політики образів мати не може.
- **Ключ ЦА** — сутність `AggregatorSigningKey` (зашифрована приватна частина, публічна, `CreatedAt`, `RetiredAt`): активний один, вилучені лишаються, щоб панель показувала їхні публічні ключі. Окрема таблиця, а не налаштування системи, бо налаштування не зберігають історію.
- **Кеш тегів** — таблиця `ImageTagCacheEntry` (`RegistryHost`, `Repository`, `Tag`, `Digest`, `Created`, `FetchedAt`), спільна для всіх політик з тим самим образом. У пам'яті не тримати: після рестарту ЦА перша перевірка інакше повторювалася б.
- **Послідовність по стеку:** один екземпляр ЦА, тож досить `SemaphoreSlim` на стек у singleton `ImageUpdateLocks`. Той самий замок бере і опитування, і хук образів, і дії людини.
- **Налаштування системи** (ADR-0013): секція `ImageUpdates` — `PollingEnabled` (так), `PollingInterval` (5 хв, мінімум 1 хв), `MaxTagReadsPerCheck` (100), `RegistryTimeout` (30 с).
- **Деплой оновлення** — `DeploymentTrigger.ImageUpdate`, `Deployment.QueueFromImageUpdate(stackId, commit, recordId, now)`: закріплений коміт, як із `approveCommit`, щоб `GitFetchStep` брав саме його. `IsAutomatic = true`. Політика pull — `Missing`: тег новий, тож образ однаково тягнеться.
- **Ендпойнти** (усі в OpenAPI, контракти в `Api/Contracts/ImageUpdateContracts.cs`):

  | Метод і шлях | Що |
  |---|---|
  | `GET/POST /api/stacks/{id}/image-policies`, `PUT/DELETE …/{policyId}` | політики стеку |
  | `POST …/{policyId}/check` (`?dryRun=true`) | перевірити зараз; з `dryRun` — лише показати, що змінилося б |
  | `POST …/{policyId}/pin` `{tag}`, `POST …/{policyId}/unpin` | закріплення людиною |
  | `PUT/DELETE /api/stacks/{id}/image-hook` | хук образів стеку (`PUT` — новий секрет і `allowDowngrade`) |
  | `POST /api/image-hooks/{stackId}` | виклик хука (анонімний ендпойнт, як хук деплою) |
  | `PUT/DELETE /api/git-connections/{id}/push-credential` | обліковий запис для запису |
  | `GET /api/system/signing-key`, `POST …/rotate` | ключ ЦА |

- **CLI:** `forest stack image-policy list|add|change|remove|check|pin|unpin`, `forest stack image-hook set|remove`, `forest connection push-credential set|remove`, `forest system signing-key show|rotate`.
- **Аудит:** `ImagePolicyAdded/Changed/Removed`, `ImagePinned/Unpinned`, `ImageHookChanged/Removed`, `GitPushCredentialChanged`, `AggregatorSigningKeyRotated`. Сам коміт оновлення — подія деплою, окремого запису аудиту не треба.

## Етапи

### Етап 1. Модель, зберігання, керування

- Сутності й ідентифікатори з розділу вище, конфігурації EF, одна міграція `ImageUpdates`.
- `ImagePolicy`: валідація при створенні й зміні (ADR п. 1): `TagPattern` компілюється з тайм-аутом і закінчується на `\z`; для `TagTimestamp` є група `ts`; збіг хоча б з одним сервісом compose-файлу на голові гілки; відмова для `${…}`, `extends` без `image`, `include`. Голову й compose читати наявними `IGitSource` і `ComposeFile`.
- Хук образів на `Stack`: встановлення, ротація, видалення Editor стеку, 409 для системного стеку.
- `PushCredential` на `GitConnection`, ендпойнти й права — як для наявного токена доступу підключення.
- API, CLI, аудит для всього вище. Без жодної автоматики: політики лише зберігаються й показуються.
- **Готово, коли:** політику можна створити, змінити, видалити з CLI й API; невалідні політики відхиляються зрозумілими помилками; секрет хука показується один раз; тести домену й API зелені; міграція застосовується до копії продакшн-бази без втрат.

### Етап 2. Клієнт registry і вибір тегу

- `IRegistryClient` (Application) і `OciRegistryClient` (Infrastructure): `ListTagsAsync` (пагінація за `Link`, `n=1000`), `GetManifestAsync` (заголовки `Accept` для OCI index, OCI manifest, Docker manifest list і v2), `GetConfigCreatedAsync`. Digest — з `Docker-Content-Digest`, інакше sha256 тіла; для індексу `created` — з маніфесту першої платформи `linux/*`.
- Автентифікація: анонімно → на 401 розібрати `WWW-Authenticate: Bearer realm,service,scope` і взяти токен з Basic-кредами середовища (`RegistryCredential.Serves`), або Basic напряму. Креди — середовища стеку, як у `ResolveRegistryAuthStep`.
- Захист від SSRF — той самий, що для git-підключень (приватні й loopback-адреси, `Git:AllowPrivateHosts`); не писати другий.
- `ImageTagSelector` (Application, без I/O): фільтр `TagPattern`, порядок `ImageCreated` / `TagTimestamp`, правила ADR п. 2 (незавершений каталог — не оновлювати; однакові `created` — помилка політики), ліміт `MaxTagReadsPerCheck`.
- `POST …/check?dryRun=true` і `forest stack image-policy check --dry-run`: показують поточний тег, кандидата й причину, нічого не змінюючи.
- **Готово, коли:** dry run на справжньому `gitea.rup.rocks/lepterion/ananasik/ananasik` показує найновіший `main-*`; тести клієнта на фейковому HTTP-сервері (401 → токен → 200, пагінація, індекс, 429); тести селектора на всі правила порядку.

### Етап 3. Зміна compose і запис у git

- `ComposeImageRewriter` (Application, без I/O): YamlDotNet `Parser`, для `services.<name>.image` взяти `Start.Index`/`End.Index` скаляра й замінити підрядок, зберігши стиль лапок. Після заміни розібрати файл знову й перевірити, що змінилися лише ці значення. Повертає новий текст і перелік змін (`service`, `from`, `to`).
- Ключ ЦА: генерація `ssh-ed25519` при першій потребі (`ssh-keygen -t ed25519 -N '' -f …` у тимчасовому каталозі з правами 0700, файл видаляється одразу), зберігання через `ISecretProtector`. `ICommitSigner` / `SshKeygenCommitSigner`: `ssh-keygen -Y sign -n git -f <тимчасовий файл ключа>`, дані — stdin, підпис — stdout, через `CliProcess`, як `SshKeygenSignatureVerifier`.
- `SigningKeyService` відхиляє відбитки будь-якого `AggregatorSigningKey`, активного чи вилученого.
- `IGitWriter` / розширення `LibGit2SharpGitSource`: у bare-кеші під тим самим замком, що й fetch: `TreeDefinition.From(parent.Tree)` з новим blob (режим файлу зберегти), `Commit.CreateBuffer`, `ObjectDatabase.CreateCommitWithSignature`, push `<sha>:refs/heads/<branch>` з `PushCredential`. Відмова non-fast-forward — окремий тип помилки для повтору.
- `ImageUpdateService.ApplyAsync(policy, tag, reason, requestedBy)`: замок стеку → fetch → перевірка голови як автоматичного тригера ADR-0011 (винести з `VerifyCommitStep` спільну перевірку засвідчення в окремий сервіс, а не дублювати) → переписати → коміт і підпис → push з трьома спробами → `ImageUpdateRecord`. Деплою ще немає.
- **Готово, коли:** інтеграційний тест на локальному bare-репозиторії: коміт з одним зміненим рядком, байти решти файлу збігаються, підпис перевіряється `ssh-keygen -Y verify`, повтор після конкурентного push працює; тести переписувача на коментарі, лапки, кілька сервісів, фільтр `Services`, `PinDigest`.

### Етап 4. Засвідчення й деплой оновлення

- `ICommitInspector`: метод, що повертає змінені шляхи й вміст compose-файлу батька й коміту (для перерахунку різниці).
- `VerifyCommitStep`: якщо оцінка підпису дала `Unsigned` або `UnknownKey` (ключ ЦА навмисно невідомий), шукати `ImageUpdateRecord` за (стек, SHA) і засвідчити за ADR п. 6: єдиний батько = `Parent`; змінено лише compose-файл стеку і лише записані `image`; батько засвідчений (рекурсивно через записи, до 100 кроків). Новий `CommitEndorsement.ImageUpdate`, `Deployment.RecordImageUpdate(recordId, rootSigner…)`, права власника кореня — на момент деплою.
- `DeploymentTrigger.ImageUpdate`, `QueueFromImageUpdate`, виклик з `ImageUpdateService` після push. Webhook від власного push має давати `AlreadyRunning`/`AlreadyDeployed` — перевірити тестом з `PushDeploymentGate`.
- Панель і CLI показують новий тригер і статус засвідчення (історія деплоїв, `forest deployment show`).
- **Готово, коли:** тести кроку на всі гілки (запис є / нема, підмінене дерево, зайвий файл, незасвідчений батько, ланцюжок із кількох оновлень, ліміт ланцюжка); ручний `check` без `dryRun` на тестовому стеку робить коміт і деплоїть його з `ImageUpdate`.

### Етап 5. Запуск оновлень: хук, опитування, явні теги, закріплення

- `POST /api/image-hooks/{stackId}` (`ImageHookService`): автентифікація секретом стеку (однаковий 401), політики стеку з `AcceptRequests` (усі або за `image`), відповідь — перелік політик із результатом.
- Явний тег: перевірки ADR п. 3, напрям (новіший / старіший) за порядком політики, `AllowDowngrade`, закріплення. Закріплення людиною — ендпойнти `pin`/`unpin`.
- `ImageUpdatePoller` (hosted service): раз на `PollingInterval` — політики з `Poll`, без закріплення, стек з онлайн-агентом; кожна політика — через `ImageUpdateService` під замком стеку. Помилка однієї політики не зупиняє інших.
- **Готово, коли:** тести сервісу хука (секрет, `AcceptRequests`, downgrade, закріплення) і опитувача (на `FakeTimeProvider`); на тестовому стеку новий образ підхоплюється опитуванням, а виклик `curl` хука оновлює одразу.

### Етап 6. Звільнення, сповіщення, панель

- `ComposePolicyStep`: звільнення засвідченого вмісту для `ImageUpdate` — лише якщо корінь ланцюжка дає звільнення за ADR-0012 і `LastChangedByAdministrator` політики (ADR п. 7).
- Сповіщення: `NotificationEventKinds.ImageUpdateFailed = 1 << 10`, `ImagePinned = 1 << 11`; `NOTIFICATION_KIND_IMAGE_UPDATE_FAILED = 11`, `NOTIFICATION_KIND_IMAGE_PINNED = 12` у `bot.proto`; правило «одна помилка до успішної перевірки» через `FailureNotified`; embed у боті; вибір подій у правилах панелі.
- Панель:
  - стек — розділ «Політики образів»: перелік, створення й зміна, стан останньої перевірки, «Перевірити» з dry run, закріплення й зняття; попередження `TagTimestamp` (понад 500 кандидатів або 429);
  - хук образів — поруч із хуком деплою на екрані стеку, з показом секрету один раз;
  - підключення — обліковий запис для запису (`features/git-connections`);
  - налаштування системи — ключ ЦА: публічна частина, вилучені ключі, ротація з підтвердженням особи (ADR-0013).
- **Готово, коли:** тести кроку політики й сповіщень; екрани працюють локально (`npm run dev` проти ЦА в `Development`); переклади в `i18n`.

### Етап 7. Документація й облік

- `docs/design/image-updates.md`: де що в коді, як у `signed-commits.md`. Рядок у таблиці «Де що шукати» кореневого `CLAUDE.md` (ліміт 150 рядків).
- Оновити: `architecture.md` (розділ хука деплою — новий шлях для незмінних тегів), `design/deployment.md` (тригер, крок перевірки), `design/signed-commits.md`, `design/git.md` (запис), `design/notifications.md`, `design/web-panel.md`, `design/decisions.md`, `project-status.md`.
- ADR-0015 → «прийнято», рядок у `docs/adr/README.md` без «Запропоновано». Позначки в ADR-0006 і ADR-0011 про зміну з посиланням на 0015.
- `CONTRIBUTIONS.md`, розділ Петренка Б. О.: перелік у «Код» і перерахунок таблиці лаби 1; екрани — у лабу 2.
- Видалити цей план.
- **Готово, коли:** `scripts/check-docs` зелений (ідентифікатори в документах існують у коді), DocFX збирається.

## Журнал

_Порожньо: заповнюється наприкінці кожного етапу._
