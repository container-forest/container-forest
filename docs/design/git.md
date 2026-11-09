# Git і підключення до git-серверів

Рішення — [ADR-0006](../adr/0006-git-connections.md). Шляхи ЦА відносно `src/Aggregator`.

## Клони

- **`IGitSource`** — два виклики: `FetchAsync` повертає коміт гілки, `ReadAsync` читає каталог compose-файлу з нього. `GitFetchStep` записує коміт у деплой між ними, тож деплой, що впав на файлах (немає каталогу, перевищено ліміт пакета), усе одно показує свій коміт.
- **Кеш:** bare-клон на кожен репозиторій у `Git:CacheDirectory` (типово `git-cache` у робочому каталозі), fetch лише гілки стеку з `Prune`, файли читаються з дерева коміту без checkout. Клон більший за `Git:MaxCacheBytes` (1 GiB) видаляється, деплой падає.
- **Пакет:** каталог compose-файлу (для compose в корені — увесь репозиторій) у межах `Deployments:MaxBundleBytes` (3 MiB). Символьні посилання й сабмодулі пропускаються з попередженням.
- **Протокол:** LibGit2Sharp з NuGet не підтримує SSH, лише HTTPS.

## Підключення

`GitConnection` (таблиці `GitConnections`, `GitConnectionEnvironments`), `GitConnectionService`, `/api/git-connections/*`.

- **Поля:** назва (унікальна), префікс адреси, дозволені середовища або «усі», секрет webhook організації, необов'язковий токен з ім'ям користувача (типово `x-access-token`). Токен зашифрований `ISecretProtector` (`SecretPurpose.GitAccessToken`) і ніколи не повертається, лише `hasAccessToken`.
- **Доступ:** керує лише адміністратор. Редактор бачить підключення своїх середовищ, і лише свої середовища в `environmentIds`.
- **Обмеження змін:**
  - Префікс не може лишити поза собою репозиторії підключення (409).
  - Хост при заданому токені не змінюється (400).
  - Підключення з репозиторіями не видаляється (409, FK `Restrict`).

## Репозиторії

- **Модель:** `GitRepository` має URL і необов'язковий `ConnectionId`. Без нього репозиторій публічний, доступний усім середовищам. Назви не унікальні.
- **Адреса:**
  - URL має бути під префіксом. `GitUrl.Covers` порівнює хост і порт та сегменти шляху, без урахування регістру й із відкиданням `.git`.
  - `GitUrl.Parse` відхиляє облікові дані, query, fragment і будь-яке `%`-кодування в шляху, бо закодоване `..` чи `/` обійшло б префікс.
- **Права** (`RepositoryAccess`). «Редакторські середовища» — де користувач Editor на середовищі чи на стеку (`AccessPolicy.EditorEnvironments`).
  - Додати репозиторій може адміністратор або редактор, якщо підключення дозволене в одному з його середовищ. Публічний — будь-який редактор.
  - Змінити репозиторій і його webhook може адміністратор або той, хто має `Permission.Deploy` на кожному стеку репозиторію (`RepositoryResponse.canManage`).
  - Недоступне підключення чи репозиторій — 404.

- **Токен запису** (ADR-0015): необов'язковий `GitConnection.ProtectedPushToken` (`SecretPurpose.GitPushToken`) і `PushUserName`, `PUT|DELETE /api/git-connections/{id}/push-credential`, лише адміністратори, в аудиті (`GitPushCredentialChanged`). Ним ЦА пушить коміти оновлень образів, токен читання для цього не береться ніколи (`GitAccessResolver`). Запис — `IGitWriter` у тому самому bare-клоні, що й fetch ([image-updates.md](image-updates.md#перевірка-й-коміт)).

## Fetch і захист токена

- **Перевірка:** `GitFetchStep` перевіряє `IsUsableIn` до fetch і передає `GitAccess` (токен підключення з `Scope` = префікс; `IsTrustedHost` для репозиторіїв підключення).
- **Токен:** `LibGit2SharpGitSource.CredentialsFor` віддає його лише по HTTPS на адреси під префіксом.
- **SSRF:** для репозиторію без підключення `GitHostGuard` резолвить хост і відмовляє на loopback, приватних, CGNAT, link-local (metadata хмари), multicast адресах, якщо не задано `Git:AllowPrivateHosts`. Помилка fetch такого репозиторію в лозі деплою узагальнена, деталі libgit2 — у лозі ЦА.

## Міграції

`GitConnections` (схема), `MoveRepositoryTokensToConnections` (кожен репозиторій з токеном отримує власне підключення з тим самим id, префіксом — своїм URL і середовищами своїх стеків), `RemoveRepositoryAccessTokens`. Дві з них перебудовують таблицю `Repositories` у SQLite поза транзакцією, звідси попередження EF при старті.
