# Автентифікація, користувачі, токени

Рішення — [ADR-0007](../adr/0007-api-tokens.md) (токени), [ADR-0008](../adr/0008-cli.md) (CLI), [ADR-0009](../adr/0009-passwordless-sign-in.md) (passkeys, Discord). Шляхи бекенду відносно `src/Aggregator`, панелі — відносно `src/Web`.

## Спільний договір

- **Автентифікація й авторизація розділені одним договором:** будь-яка схема (cookie, API-токен, dev-заголовок, бот) кладе `UserId` у claim `ClaimTypes.NameIdentifier`; `ClaimsCurrentUser` читає його як `ICurrentUser`, а сервіси `Application` отримують користувача лише через `CurrentActor` (немає користувача — 401, деактивований — 403). Контракт логіну — `Api/Contracts/AccountContracts.cs`.
- **Схема за замовчуванням** — policy `Default` (`AuthenticationSetup`): Bearer → `ApiToken`, у `Development` заголовок `X-Dev-User` → `DevUser`, інакше cookie. Поза `Development` заголовок ігнорується. `DevUser` називає будь-якого користувача, створеного через `/api/users` або bootstrap, без пароля; ним користуються curl і `CF_DEV_USER` панелі.

## Пароль і користувачі

- **Вхід за паролем.** Паролі, lockout і security stamp — в ASP.NET Identity: `AccountUser : IdentityUser<Guid>` (`Infrastructure/Accounts`) з тим самим id, що й доменний `User`; `AggregatorDbContext` успадковує `IdentityUserContext<AccountUser, Guid>` (таблиці `AspNetUsers*`, без таблиць ролей). Зовнішнього ключа `AspNetUsers` → `Users` немає: EF не з'єднує `Guid` із `UserId`; обліковий запис створюється разом із доменним користувачем в одному `SaveChanges` (`UserManager.CreateAsync` зберігає контекст). Користувачі, створені колись через видалений `/api/dev/users`, облікового запису не мають: його створює скидання пароля адміністратором.
- **Керування користувачами** (`/api/users/*`, контракт — `Api/Contracts/UserContracts.cs`): `UserManagementService`, усе лише для адміністраторів. Облікові записи Identity сервіс змінює лише через порт `IAccountStore` (`IdentityAccountStore`); кожна зміна облікового запису зберігає контекст разом із доменними змінами. Пароль перевіряється політикою до зміни хешу, щоб невдалий пароль не лишив облікового запису без пароля.
  - Зміни користувача — ієрархія `UserAccountAction` (Command): перевірка → дія → завершення сесій (`EndsSessions`). `AdministratorRemovingAction` (деактивація, зняття прав адміна) не дає прибрати останнього активного адміністратора (409). Деактивація, зняття прав адміна й скидання пароля завершують сесії користувача; перейменування — ні (`SetUserNameAsync` Identity міняв би security stamp, тому ім'я змінюється через `UpdateAsync`).
  - Ролі: одна на область (середовище або стек), друга — 409; стек іншого середовища й чуже призначення — 404.
  - Політика — `AccountPolicy`: мінімальна довжина без правил про склад і lockout. Решту NIST SP 800-63B перевіряють правила `PasswordRule` (`IPasswordValidator` Identity, `Infrastructure/Accounts/PasswordRules.cs`): обмеження довжини, без повторів і послідовностей, без імені користувача й назви системи. Код помилки правила починається з `Password`: так `IdentityAccountStore` відрізняє відхилений пароль від інших помилок Identity. Відхилений пароль — `PasswordRejectedException` → 400 з `errors` у problem details.
  - `PasswordSignIn`: невідоме ім'я, неправильний пароль, lockout і деактивований користувач — однаковий 401; для невідомого імені все одно рахується хеш (decoy), щоб час відповіді не видавав наявні імена.
  - `/api/auth/login` і `/api/auth/password` мають ліміт на адресу клієнта (`AuthEndpoints.SignInPolicy`, інакше 429): lockout захищає один обліковий запис, але не один пароль, який пробують на багатьох іменах. За Caddy адреса — з `X-Forwarded-For`.
  - Cookie `cf_session`: HttpOnly, `SameSite=Strict`, `Secure` — `Always`, у `Development` — `SameAsRequest`; `RememberMe` — постійна cookie, інакше сесійна. Security stamp перевіряється на кожному запиті (`ValidationInterval = 0`): зміна пароля завершує інші сесії одразу, поточна перевидається (`RefreshSignInAsync`). Ціна — запит до БД і нова cookie на кожну відповідь.
  - Перший адміністратор: `AdminBootstrapper` після міграцій, якщо в базі немає жодного користувача, створює його з `CF_BOOTSTRAP__ADMINUSERNAME` / `CF_BOOTSTRAP__ADMINPASSWORD` (задаються разом або жодна, `ValidateOnStart`). Пароль не логується; без змінних і без користувачів — попередження в лозі.

## API-токени

Формат, правила обмежень і причини — в ADR-0007.

- **Код:** `Domain/Access/ApiToken.cs`, `Api/Access/ApiTokenAuthenticationHandler.cs`, `/api/me/tokens`, `/api/users/{id}/tokens`. Підказка в БД — перші 6 символів після префікса. Bearer іншої форми (хук деплою) схема пропускає (`NoResult`).
- **Обмеження** — у claims (`ApiTokenClaims`: `cf:api-token`, `cf:read-only`, `cf:restricted`, `cf:environment`), `ClaimsCurrentUser` віддає їх як `ICurrentUser.Scope` і `ApiTokenId`. Рішення приймає `AccessPolicy` на `Actor(User, AccessScope)`.
- **Лише читання:** `ReadOnlyTokens` (middleware після автентифікації) відповідає 403 на будь-який не-GET/HEAD/OPTIONS запит, крім ендпоінтів з `AllowReadOnlyTokens()`.
- **Лише сесія:** новий сервісний метод, що змінює сам обліковий запис або видає облікові дані, бере користувача через `CurrentActor.GetSignedInUserAsync`. Live-хаб — політика `Session`.

## Passkeys

Потік, cookie стану й правила керування — в ADR-0009.

- **Код:** `Api/Access/PasskeySignIn.cs`, `PasskeyManagement.cs`, `PasskeyContracts.cs`.
- **Пастка міграцій.** Схема Identity — версія 3 (`AccountPolicy`: `Stores.SchemaVersion`). `IdentityUserContext` будує модель з `IdentityOptions` сервісів застосунку, а не з властивості `SchemaVersion`: `DesignTimeDbContextFactory` передає `AccountPolicy.Configure` через `UseApplicationServiceProvider`, інакше міграції не бачать таблиці passkeys.
- **Вхід:** `MakePasskeyRequestOptionsAsync(null)` → `PerformPasskeyAssertionAsync`, перевірка lockout і активності доменного користувача, `AddOrUpdatePasskeyAsync` для лічильника, `SignInAsync(isPersistent: rememberMe)`. Опції — сирий JSON Identity (`TypedResults.Text`).
- **Керування:** id — credential id у base64url; атестація приймається, лише якщо `UserEntity.Id` — поточний користувач.
- **Тести:** `tests/Aggregator/Api/PasskeyTests.cs` з програмним автентифікатором `FakeAuthenticator` (ES256, CBOR через `System.Formats.Cbor`, атестація `none`); запити тесту мають заголовок `Origin`.

## Discord OAuth2

Потік, вибір `DiscordLink` замість `AspNetUserLogins` і робота навколо `SameSite=Strict` — у доповненні до ADR-0009.

- **Код:** `Api/Access/DiscordSignIn.cs`, `Endpoints/DiscordAuthEndpoints.cs`.
- **Реєстрація схеми:** лише з `DiscordAuth:ClientId` і `ClientSecret` (`DiscordAuthOptions.IsConfigured`), інакше ендпоінти — 404. `SignInScheme` — `IdentityConstants.ExternalScheme`.
- **Сервіс:** вхід — `DiscordLinkService.FindUserForSignInAsync`, прив'язка — `LinkVerifiedAsync`.
- **Стан:** мета (`cf.purpose`), адреса повернення й id користувача прив'язки — в `AuthenticationProperties.Items`. Сторінка замість 302 — `ContinuePage`, адреса повернення перевіряється в `SafePath`, невдача обробляється й в `OnRemoteFailure`.
- **OpenAPI:** ендпоінти — навігації браузера, тому не описуються (`ExcludeFromDescription`).
- **Тести:** `DiscordSignInTests` з фальшивим Discord на `BackchannelHttpHandler` (через `AggregatorApp.Customize`).

## Вхід у CLI

Потік, строки й ліміти — в ADR-0008.

- **Код:** `CliAuthorizationService`, `Domain/Access/CliAuthorization.cs`, `CliAuthorizationEndpoints`. `userCode` генерує `OneTimeCode`, показується як `XXXX-XXXX`.
- **Обмеження токена** перевіряються як для `/api/me/tokens` (`ApiTokenService.EnsureRoomForTokenAsync`, `EnsureEnvironmentsVisibleAsync`).
- **Видача:** токен створює `ExchangeAsync` (`CliAuthorization.IssueToken`), запит після цього видаляється; деактивований після затвердження користувач — `Denied`.
- **Ліміти запитів:** за Caddy адреса клієнта — з `X-Forwarded-For`.
- **Панель:** `/cli/authorize?code=&port=` (після рішення — `window.location.assign("http://127.0.0.1:<port>/done?result=…")`) і `/cli/device`.
