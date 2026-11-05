# Сповіщення

Коли саме надсилається подія, описують коментарі `ContainerIncidentWatch` і `AgentConnectivityWatch`; типові значення — `NotificationOptions`. Тут — решта. Шляхи `Domain/…`, `Application/…`, `Api/…` відносно `src/Aggregator`.

## Доставка й домен

- **Сповіщення** доставляються в Discord через живого бота, а не webhook'ом каналу: бот тримає server-stream `WatchNotifications` і звітує `ReportDelivery` про кожне (`bot.proto`). Так URL webhook'ів (фактично секрети) ніде не зберігаються, а бот додає кнопки до повідомлення, і натискання йде звичайним `ControlContainer` чи `GetContainerLogs` з правами того, хто натиснув. Ціна: без підключеного бота сповіщення не доставляються; вони лишаються `Pending` і надсилаються після підключення, а старші за годину — `Expired`.
- Домен сповіщень (`Domain/Notifications`): `NotificationChannel` (Discord-канал), `NotificationRule` (які події, з якої області: уся система, середовище або стек; `Matches`), `NotificationEvent` (одна подія з рівно одним видом) і `Notification` (запис історії: Pending → Delivered | Failed | Expired). Канали й правила керуються лише адміністраторами: Discord-канал можуть читати люди без жодної ролі. Історія не має зовнішніх ключів на правила й канали, щоб переживати їх видалення.
- Сповіщення в ЦА: джерело подій викликає `NotificationDispatcher.DispatchAsync(event, summary)`; диспетчер створює одне `Notification` на канал (від найконкретнішого правила, що збіглося), зберігає й будить потік бота через `NotificationFeed`. Деплої — через `IDeploymentObserver` (`DeploymentNotifier`), який `DeployPipeline` викликає після збереження результату; спостерігач сам логує свої помилки й не кидає. `Notification.Details` — знімок деталей події (`DeploymentNotificationDetails`), зберігається JSON-колонкою (`NotificationDetailsConverter`), щоб сповіщення, доставлене після перепідключення бота, було тим самим. Канал і назву середовища потік бере на момент надсилання: переміщений канал отримує й старі сповіщення, а сповіщення видаленого чи вимкненого каналу стають `Failed`.
- Бот: `NotificationWatcher` (hosted service) чекає `Ready` Discord, тримає потік через `INotificationSource` (`GrpcNotificationSource`) і перепідключається з backoff; кожне сповіщення публікує `INotificationPoster` (`DiscordNotificationPoster`, embed з `AllowedMentions.None`) і звітує результат. Відмова Discord (немає каналу, бракує прав) — `delivered = false` з підказкою для панелі; невдалий звіт лише логується: ЦА надішле сповіщення знову в наступному потоці. Канал бота шле HTTP/2 keepalive-пінги, бо потік може мовчати годинами.
- **Налаштування сповіщень у панелі** (`NotificationSettingsService`, `Api/Contracts/NotificationContracts.cs`): канали, правила й історія — лише для адміністраторів. Discord id у JSON — рядки (64-бітні числа JavaScript округлює). Події правила — список окремих видів, у домені — прапорці `NotificationEventKinds`; порожній список — 400. Правило на стек може не вказувати середовище, а вказане чуже — 400. Область правила не змінюється (видалити й додати нове), змінюються лише події й увімкнення. Видалення каналу забирає його правила (каскад), історія лишається з `channelName = null`.

## Агент офлайн

`AgentConnectivityWatch` (singleton), `AgentConnectivityNotifier` (scoped). `AgentGatewayService` повідомляє сторожа після `registry.Open` і після `registry.Remove` поточної сесії. Повторний розрив під час очікування простою не зсуває. Видалене середовище не сповіщається. Подія без стеку: правила на стек її не отримують.

## Падіння й unhealthy контейнера

`ContainerIncidentWatch` (singleton), `ContainerIncidentNotifier` (scoped). `AgentGatewayService` передає кожен `ContainerEvent` як `ContainerObservation`.

- **Послідовності подій** з коментаря класу перевірено на Docker 29. `docker kill` без stop теж вважається падінням.
- **Код виходу** — `ContainerEvent.exit_code`, а від агента старшої версії — зі статусу Docker після події (`Exited (N)` або `Restarting (N)` для контейнера з політикою перезапуску; контейнер, який Docker уже встиг запустити знову, тоді сповіщається без коду). Код 0 не сповіщається, зокрема й перезапущена політикою `always` задача. Код понад 128 — смерть від сигналу 128 + N, у тексті з назвою (`SignalName`).
- **OOM:** текст «was killed: out of memory» замість коду 137, `ContainerNotificationDetails.OutOfMemory`. У `bot.proto` поля для OOM немає: бот бачить це лише в тексті.
- **Unhealthy:** приглушене інтервалом повтору unhealthy свого «знову здоровий» не має. У `bot.proto` — `NOTIFICATION_KIND_CONTAINER_HEALTHY`; старіший бот покаже його як «Notification» без кнопок.
- **Контейнер** рахується за ім'ям, бо перестворення змінює id.
- **Стек події** — керований стек середовища з назвою compose-проєкту; інший проєкт і контейнер поза compose — подія середовища, правила на стек її не отримують.
- **Деталі:** `ContainerNotificationDetails` (JSON-колонка, `ContainerDetails` у `bot.proto`).

## Кнопки під сповіщенням

- **Кнопки «Restart» і «Logs» під сповіщенням** (`ContainerButton`, `NotificationModule`): `DiscordNotificationPoster` додає їх до `ContainerDied` і `ContainerUnhealthy`; «Restart» — крім контейнерів, які можна лише запускати (`ContainerDetails.system` у `bot.proto`: мітка `com.container-forest.system` з `ContainerInfo.system` агента або системний стек, `ContainerNotificationDetails.IsSystem`), «Logs» — завжди. `custom_id` — `ntf:<rs|lg>:<середовище base64url>:<ім'я контейнера>` (задовге ім'я — сповіщення без кнопок), бот без стану, тож кнопки працюють і під старими повідомленнями. Ідентифікатори — `CompactGuid` (спільний із `/dashboard`).
- **«Logs»** (`ContainerLogsCommand`, `GetContainerLogs` у `bot.proto` → `ContainerLogService`, право `ViewLogs`) показує кінець логу, що вміщається в одне повідомлення (`ReplyFormatter.WithOutput`), без кольорів ANSI й керівних символів. Логи можуть містити секрети, тому відповідь, як і всі відповіді бота, бачить лише той, хто натиснув.
- **«Restart»** виконує `RestartContainerCommand` від імені того, хто натиснув (права перевіряє ЦА, неприв'язаний акаунт — підказка `/link`); відповідь — лише йому: компонент відкладається `DeferLoadingAsync(ephemeral)`, бо звичайний `DeferAsync` компонента зробив би відповідь редагуванням самого сповіщення в каналі.
- **Поле «Last action»:** успішний restart бот відзначає в самому сповіщенні (`NotificationEmbed.WithRestart`: хто й коли; `<@id>` в embed нікого не пінгує, наступний restart замінює рядок), щоб інші в каналі не перезапускали контейнер удруге; невдале редагування (повідомлення видалено) — лише в лог.
