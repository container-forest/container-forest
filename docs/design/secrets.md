# Секрети: SOPS, git-фільтр, rehydrate

Рішення — [ADR-0003](../adr/0003-secrets-at-rest.md), [ADR-0010](../adr/0010-git-secrets-filter.md). Як агент пише секрети — [src/Agent/CLAUDE.md](../../src/Agent/CLAUDE.md). Шляхи ЦА відносно `src/Aggregator`.

## Ключі

- **Модель:** `AgeKey` (таблиця `AgeKeys`) — age-пара X25519 на середовище або стек (ADR-0012). Приватна частина зашифрована `ISecretProtector` з `SecretPurpose.AgeIdentity`, у відповідях — лише `recipient`.
- **Ротація:** ключів на середовище може бути кілька: новий шифрує нові файли, усі разом розшифровують.
- **Доступ** (`AgeKeyService`): створює й видаляє адміністратор або адміністратор середовища. Публічні ключі бачить кожен, хто бачить середовище: вони потрібні для `.sops.yaml`.
- **Генерація:** пару створює `age-keygen` (`AgeKeygenCli`), вивід розбирається й ніде не логується.

## Розшифрування під час деплою

`SopsDecryptStep` виконується одразу після `GitFetchStep`.

- **Що вважається зашифрованим:** лише файл із метаданими sops свого формату (`SopsFile.IsEncrypted`), бо файл, що лише згадує sops чи `ENC[`, має деплоїтися як є. Формат визначається за розширенням, як у самого sops:

  | Формат | Ознака |
  |---|---|
  | YAML | ключ `sops:` верхнього рівня |
  | JSON | об'єкт `sops` |
  | dotenv | `sops_version=` |
  | INI | `[sops]` |
  | решта (двійкові) | `{"data", "sops"}` |

- **Ключі:** лише цільового середовища. Без ключа, з чужим ключем або з нечитабельним приватним ключем — помилка деплою з підказкою.
- **Результат:** файл іде агенту з `StackFile.secret = true` (tmpfs + символьне посилання), у лозі деплою — лише шляхи.
- **`.gitattributes`:** крок спершу перевіряє їх (`GitAttributes`, правила `.gitignore` лише для атрибута `filter`): файл під `filter=forest` без метаданих sops — помилка деплою. Файли `.gitattributes` каталогів вище стеку `LibGit2SharpGitSource` кладе в `GitSnapshot.OuterAttributes`.

## Виклик sops

- **`SopsCli`** запускає `sops decrypt --filename-override <шлях> /dev/stdin`: шифротекст іде через stdin, відкритий текст — через stdout, на диск ЦА нічого не пишеться.
- **Оточення:** ключі передаються в `SOPS_AGE_KEY` (через `\n`). `CliProcess` очищає оточення дочірнього процесу (лишається `PATH`), тож `CF_*` ЦА до sops не потрапляють.
- **Помилки:** код виходу 128 (`CouldNotRetrieveKey`) — «жоден ключ середовища не є отримувачем», решта — перший рядок stderr (при розшифруванні sops не друкує відкритого тексту).
- **Бінарники:** `sops` і `age-keygen` в образі ЦА ставить `scripts/install-sops.sh` (версії й SHA-256 для amd64/arm64 — у скрипті, оновлювати там) в окремій стадії Dockerfile. Шляхи й тайм-аут — `Sops:SopsPath`, `Sops:AgeKeygenPath`, `Sops:Timeout`.
- **Тести:** `SopsToolsTests` зі справжніми інструментами без них пропускаються, а в CI (`CF_TEST_REQUIRE_SOPS=1`, інструменти ставить той самий скрипт) падають.

## API для git-фільтра

`SopsSecretsService`, `Api/Contracts/SopsContracts.cs`, ADR-0010. Обидва ендпоінти приймають до 1 MiB вмісту й мають ліміт `sops` на користувача (token bucket: 300, +60 за 10 с).

**`POST /api/sops/decrypt`**

- **Право:** `AccessScope.CanDecryptSecrets`. Сесія його має, токен — лише з прапорцем `secrets` (claim `cf:secrets`, ніколи разом із лише читанням).
- **Ключі:** ті, чиї `recipient` є в метаданих файла (`SopsFile.AgeRecipients`, лише підказка) і чиє середовище в `AccessPolicy.EditorEnvironments` актора. Адміністратор без обмежень — усі.
- **Відмови:** жодного ключа ЦА серед отримувачів — `SopsDecryptionException` (422), лише чужі середовища — 403.
- **Лог:** хто й який шлях, без вмісту.

**`POST /api/sops/encrypt`**

- **Право:** будь-який актор, крім лише читання.
- **Правило:** `YamlSopsConfigReader` обирає перше правило `.sops.yaml` за `path_regex` (шлях від кореня репозиторію, `RegexOptions.NonBacktracking` замість RE2) і приймає лише `age` та параметри «що шифрувати». Інший тип ключа — `SopsConfigException` (400).
- **Запуск:** `SopsCli.EncryptAsync` пише тимчасовий JSON-конфіг лише з цим правилом і запускає `sops --config <файл> encrypt`. `.sops.yaml` репозиторію до sops не потрапляє, тож KMS, Vault і PGP ЦА не викликає.
- **Помилки:** помилка sops — 422, у лог лише код виходу, бо stderr може цитувати відкритий текст.

## Git-фільтр у CLI

`src/Cli/Secrets`, ADR-0010.

**Налаштування**

- `forest git setup` пише в локальний конфіг клону `filter.forest.process` (`forest git filter-process`, абсолютний шлях бінарника в одинарних лапках, якщо це сам `forest`), `filter.forest.required`, `diff.forest.textconv`, `forest.context`. Також ставить pre-commit хук (`forest secrets check --staged`; чужий хук не перезаписує) і заново checkout'ить зашифровані незмінені файли під фільтром.
- `git teardown` (`GitFilterSetup.Remove`) — протилежність `setup`: прибирає секції `filter.forest`, `diff.forest`, ключі `forest.*`, свій хук, `.git/forest` і повертає шифротекст у робочі файли. Незакомічені зміни — відмова.

**Сам фільтр**

- `GitFilterProcess` — протокол pkt-line версії 2 (`PktLineReader`/`PktLineWriter`), можливості clean і smudge.
- Кожен файл — `SmudgeOperation` або `CleanOperation` (Template Method над `FilterOperation`), бекенди — `ISopsBackend` (`AggregatorSops` через `ForestApi`, `LocalSops` з `forest.localSops`).
- Smudge без доступу повертає шифротекст і success (причина в stderr один раз); clean при будь-якій помилці — `status=error`.
- `FilterCache` (`.git/forest/cache.json`, ключ HMAC `.git/forest/key`, 0600) повертає незміненому тексту той самий шифротекст. `secrets updatekeys` забуває записи й робить `git add --renormalize`.

**`secrets remove`** знімає фільтр із файла.

- Свій рядок `.gitattributes` видаляє, для шаблону дописує `/<шлях> -filter -diff`. Якщо глибший `.gitattributes` усе одно вмикає фільтр — відкат і відмова.
- Типово в клоні повертається шифротекст із git (незакомічені зміни — відмова), `--plain --yes` індексує відкритий текст.

**`secrets rule list|add|remove`** редагує `.sops.yaml` через `SopsConfigFile`.

- Працює з рядками блокового списку `creation_rules`: коментар перед правилом належить йому, незмінені правила пишуться як були. Чужа форма (flow-стиль, блокові скаляри, списки ключів) — відмова.
- Нове правило стає перед першим, з яким перекривається, з ключами, спільними для всіх правил і не чиїми-небудь ключами середовищ (резервні).

**Закінчення рядків.** Git перетворює їх до smudge, тому:

- Рядки `.gitattributes` пишуться як `filter=forest diff=forest eol=lf` (`SecretsFiles.FilterAttributes`; старі без `eol` — `LegacyFilterAttributes`, `AddAttributes` дописує їм `eol=lf`).
- `secrets fix-eol` робить те саме для кореневого `.gitattributes`, кладе в індекс шифротекст без CR через `hash-object` + `update-index` (не `git add`: той узяв би й незакомічені зміни відкритого тексту) і перечитує файли.
- `GitFilterSetup.Refresh` перечитує й шифротекст, що відрізняється від git лише CR, і повертає stderr checkout'а (причина від фільтра).
- `setup`, `status`, `check` попереджають про правила без `eol=lf`, `check` відхиляє шифротекст із CRLF.
- Clean знімає CR з dotenv та INI (`SopsFiles.PlainForSops`) до кешу й шифрування. ЦА шифротекст не нормалізує, лише називає CRLF у помилці (`SopsCli`).

## Rehydrate

Файлові секрети живуть у tmpfs і зникають із перезавантаженням хоста. Docker піднімає контейнери раніше, ніж підключається агент, тому контейнери з файловими секретами падають до rehydrate. Сигнал для ЦА повторно надіслати секрети — новий `boot_id` у `Hello`.

- **Запуск:** `DockerEnvironment.RecordConnection` порівнює `boot_id` з попереднім. Якщо він новий, `AgentGatewayService` після `registry.Open` (деплої йдуть цьому агенту) викликає `SecretRehydrationService`.
- **Які стеки:** той ставить `Deployment.QueueRehydrate` для кожного стеку з `Stack.NeedsRehydration`: є `DeployedCommit`, останній успішний деплой мав секретні файли (`DeployedWithSecrets`, з `DeployContext.HasSecretFiles`), стек не системний. Стек із незавершеним деплоєм пропускається: той сам надішле секрети.
- **Без дії:** перший `Hello` після реєстрації (`LastBootId` = null) і перепідключення без перезавантаження нічого не деплоять. Помилка постановки лише логується, з'єднання не страждає.
- **Закріплений коміт** (`IsPinned`, тригер `Rehydrate`, без користувача, `pull: missing`): гілка могла піти вперед, а відновлення секретів не має деплоїти нічого нового. `GitFetchStep` усе одно робить fetch гілки (кеш міг зникнути), але читає закріплений коміт; недоступний git-сервер — лише попередження, коміт береться з кешу ЦА.
- **Результат** іде в історію деплоїв і сповіщення, як звичайний деплой.
- **Застереження:** джерело стеку (`ChangeSource`) після останнього деплою не відстежується, і rehydrate читає закріплений коміт за поточним джерелом.
