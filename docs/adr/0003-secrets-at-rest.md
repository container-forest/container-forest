# ADR-0003. Секрети в базі ЦА шифруються ASP.NET Core Data Protection

- Статус: прийнято
- Дата: 2026-09-30

## Контекст

ЦА має зберігати значення, які потрібні йому у відкритому вигляді: паролі й токени container registry (їх отримує агент, щоб тягнути образи), а згодом deploy-токени git-репозиторіїв і приватні age-ключі SOPS. Хешувати їх, як облікові дані агентів, не можна, тож потрібне оборотне шифрування. CLAUDE.md лишав вибір відкритим: Data Protection або власний master key з конфігурації.

Варіанти:

1. **ASP.NET Core Data Protection** з key ring у каталозі на диску ЦА. Автентифіковане шифрування, ротація ключів (типово раз на 90 днів; старі ключі лишаються для розшифрування), окремий ключ на кожне призначення через purpose-рядок. Пакет `Microsoft.AspNetCore.DataProtection` — частина платформи ASP.NET Core.
2. **Master key з конфігурації** (`CF_SECRETS__MASTERKEY`) і AES-256-GCM вручну. Ключ можна тримати окремо від бази, наприклад у секретах розгортання, але формат шифротексту, версії ключів і ротацію довелося б писати й тестувати самим.

## Рішення

1. Використовується Data Protection. Порт `ISecretProtector` в `Application` (`Protect`/`Unprotect` з `SecretPurpose`) реалізує `DataProtectionSecretProtector` в `Infrastructure`. `Domain` бачить лише зашифроване значення (`RegistryCredential.ProtectedPassword`, `GitRepository.ProtectedAccessToken`).
2. Кожен вид секрету має власний purpose (`ContainerForest.Secrets.<SecretPurpose>`), тож пароль registry не розшифрується як git-токен.
3. Key ring лежить у `DataProtection:KeyDirectory` (`CF_DATAPROTECTION__KEYDIRECTORY`), типово `data-protection-keys` у робочому каталозі ЦА. `ApplicationName` — `container-forest`.
4. Значення, яке не вдалося розшифрувати (втрачений key ring, пошкоджений запис), дає `SecretUnreadableException`. Крок пайплайну показує користувачу, що креди треба задати заново, і не розкриває деталей.
5. Під час генерації OpenAPI-документа (хост стартує в збірці) використовується ефемерний провайдер, щоб збірка не створювала key ring.

## Наслідки

- **Втрата key ring означає втрату всіх збережених секретів.** Каталог ключів треба бекапити разом із базою і монтувати в контейнер ЦА як том.
- На Linux ключі на диску не зашифровані, це задокументований режим Data Protection. Захист — права доступу до каталогу (лише користувач ЦА). Хто прочитає і базу, і key ring, прочитає й секрети. Це той самий рівень, що й у master key поруч із базою; шифрування key ring сертифікатом (`ProtectKeysWithCertificate`) можна додати пізніше без зміни формату даних.
- Нової залежності для лаби 3 немає: пакет є частиною ASP.NET Core.
- Git-токени й age-ключі використовуватимуть той самий `ISecretProtector` з новими значеннями `SecretPurpose`.
