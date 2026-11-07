# Налаштування системи й бекап ЦА

Рішення — [ADR-0013](../adr/0013-system-settings.md) і [ADR-0014](../adr/0014-aggregator-backup.md). Шляхи ЦА відносно `src/Aggregator`.

## Налаштування

`Application/Settings`: `SettingsSection` (поля), `SettingsStore` (читання й запис), `StoredSetting` у `Domain/Settings` (одне поле в БД).

- **Пріоритет поля:** змінна `CF_<СЕКЦІЯ>__<ПОЛЕ>` → БД → типове значення. `ResolvedSetting` каже, звідки значення (`Source`) і чи заблоковане воно (`IsLocked`).
- **Запис:** `PreviewAsync` показує результат без збереження, щоб перевірити секцію цілком. `SaveAsync` повертає назви полів, що справді змінилися. Значення, рівне типовому, рядка в БД не має.
- **Некоректна змінна** не валить старт: поле отримує `Problem`, видиму в панелі й у статусі.
- **Нова секція:** клас-нащадок `SettingsSection` із полями й валідацією, типізований вигляд, контракти з `SettingTextResponse` і `SettingSecretResponse`, ендпоінти, екран. Сервіс секції сам перевіряє права, викликає `IdentityConfirmations.Require` і пише в `IAuditTrail`.

## Підтвердження особи

`IdentityConfirmations` видає й перевіряє токен: зашифровані Data Protection id користувача й час закінчення, 5 хвилин, без стану на сервері. `Api/Access/IdentityConfirmer` перевіряє пароль або passkey; лише для сесії, з обмеженням частоти як у входу.

- `POST /api/auth/confirm/password`, `POST /api/auth/confirm/passkey/options`, `POST /api/auth/confirm/passkey`.
- Невдача — 403, а не 401: сесія ціла, і 401 відправив би панель на екран входу.
- Дію `IdentityConfirmed` з результатом пише аудит.
- Те саме підтвердження потрібне для `POST /api/me/passkeys`, якщо в акаунта є пароль або passkey (`PasskeyManagement.NeedsConfirmationAsync`). Панель дізнається про це з `GET /api/me/passkeys/needs-confirmation`.
- **Свідомо не закрито:** адміністратор може скинути пароль чи створити адміністратора й підтвердити дію ним. Про це сповіщає подія `AccountSecurityChanged`; див. [ADR-0013](../adr/0013-system-settings.md).

## Бекап

`Application/Backups`, `Infrastructure/Backups`, `Domain/Backups/BackupRun.cs`, `Api/Contracts/BackupContracts.cs`.

| Поле | Змінна | Типове | Примітка |
|---|---|---|---|
| `Enabled` | `CF_BACKUP__ENABLED` | `false` | Лише розклад; «Зробити бекап зараз» працює й без нього |
| `IntervalHours` | `CF_BACKUP__INTERVALHOURS` | `24` | 1–168 |
| `Recipient` | `CF_BACKUP__RECIPIENT` | — | Публічний age-ключ |
| `Endpoint` | `CF_BACKUP__ENDPOINT` | — | `https://`, без шляху; `http://` лише для loopback |
| `Bucket` | `CF_BACKUP__BUCKET` | — | |
| `Region` | `CF_BACKUP__REGION` | `auto` | |
| `Prefix` | `CF_BACKUP__PREFIX` | порожньо | Початок імені об'єкта, наприклад `forest/` |
| `AccessKeyId` | `CF_BACKUP__ACCESSKEYID` | — | |
| `SecretAccessKey` | `CF_BACKUP__SECRETACCESSKEY` | — | Лише на запис |

**Запуск** (`BackupRunner`): `SqliteBackupArchiver` робить архів, `S3BackupStorage` завантажує, `BackupRun` записує результат (зберігаються 100 останніх). Об'єкт: `<prefix>forest-<час UTC>.tar.gz.age`. Крах посеред запуску не лишає рядка, і розклад просто пробує знову; залишки в `.backup-tmp` прибирає наступний запуск.

**API:** `GET`/`PUT /api/settings/backup`, `GET /api/backup`, `POST /api/backup/run` (чекає кінця; невдалий бекап — 200 з `outcome: Failed`, неповні налаштування чи вже запущений бекап — 409). Читати стан і запускати може адміністратор із токеном без обмежень (`forest backup status`, `forest backup run`), змінювати — лише в панелі (`/backup`).

## Налаштування на Cloudflare R2

1. Створити бакет у Standard storage: безкоштовний обсяг на Infrequent Access не діє.
2. Додати bucket lock rule на 30 днів і правило життєвого циклу на видалення через 90.
3. Створити API-токен `Object Read & Write` лише на цей бакет; адміністративний не давати. Окремий токен `Object Read` зберегти поза ЦА: він потрібен для відновлення.
4. Згенерувати ключ на окремій машині: `age-keygen -o forest-backup.key`. Приватну частину зберегти поза системою у двох місцях.
5. На екрані «Бекап» заповнити поля, натиснути «Зробити бекап зараз», перевірити об'єкт у бакеті й лише тоді ввімкнути розклад.
6. Додати правило сповіщень на всю систему для `BackupFailed`, `SettingsChanged` і `AccountSecurityChanged`, увімкнути 2FA в акаунті Cloudflare: зняти блокування можна через його панель.

## Відновлення

Потрібні: приватний age-ключ, ключ читання бакета, `.env` хоста. Образ ЦА не старіший за той, що зробив бекап.

```sh
# Завантажити об'єкт (за потреби: aws s3 cp --endpoint-url <endpoint> s3://<bucket>/<об'єкт> .)
age --decrypt --identity forest-backup.key forest-<час>.tar.gz.age | tar -xz -C restored/

docker compose stop aggregator
# У томі /data: прибрати aggregator.db*, покласти з restored/ aggregator.db і data-protection-keys/.
# Власник файлів — користувач образу ЦА.
docker compose start aggregator
```

Міграції застосовуються при старті, клони git отримуються заново. **Після відновлення:**

- Перевипустити API-токени й перевірити користувачів і ролі: стара база повертає те, що було відкликано після бекапу.
- Заново зареєструвати агентів, створених після бекапу.
- Задеплоїти стеки, що змінилися після бекапу: база пам'ятає старий коміт.

Цей порядок (архів з бакета, розшифрування, старт ЦА, вхід, читання збереженого секрету, новий бекап) перевірено вручну на RustFS через HTTP. На Cloudflare R2 ще не перевірявся: форму запиту через HTTPS перевіряє лише тест із локальним сервером.

## Хто бекапить весь диск сам

Функцію можна не вмикати, але знімок має бути атомарним (ZFS, LVM, знімок провайдера), інакше `aggregator.db` без узгодженого `-wal` втрачає свіжі дані. Бекапити треба разом `aggregator.db*` і `data-protection-keys/`.

## Пастки

- Через HTTPS SDK типово підписує потоково, а R2 цього не приймає. Тому `S3BackupStorage` ставить `UseChunkEncoding = false`, а через HTTP різниці не видно: перевіряти треба через HTTPS.
- Ключ у БД сам лежить у бекапі: для відновлення потрібен ключ поза ЦА.
- `aggregator.db.before-migration` лежить у томі незашифрованим, як і сама база; вміст key ring туди не входить. Він замінюється наступною міграцією, тож до того часу містить застарілі дані.
- Системні події (`BackupFailed`, `SettingsChanged`, `AccountSecurityChanged`) отримують лише правила на всю систему; екран попереджає, якщо такого правила немає.
- Мертвий ЦА про відсутність нових бекапів не повідомить: це справа зовнішнього heartbeat, якого поки немає.
