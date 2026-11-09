# Webhook, хук деплою й хук образів

Як цим користуватися — в [architecture.md](../architecture.md#деплой-після-push-webhook), webhook організації — в [ADR-0006](../adr/0006-git-connections.md). Шляхи відносно `src/Aggregator`.

## Webhook-деплой

- **Секрети:** на репозиторії (`GitRepository.ProtectedWebhookSecret`, `ISecretProtector` з `SecretPurpose.WebhookSecret`, `POST|DELETE /api/repositories/{id}/webhook`) і на підключенні (`GitConnection.ProtectedWebhookSecret`, `/api/git-connections/{id}/webhook`). Показуються один раз, новий замінює старий.
- **Вмикання:** `Stack.DeployOnPush` (`PUT /api/stacks/{id}/deploy-on-push`, для системного стеку — 409). Один webhook на репозиторій обслуговує всі стеки, а вирішує той, хто відповідає за стек.
- **Прийом:** `PushWebhookService`, для організації — `ReceiveForConnectionAsync`: репозиторій шукається за адресою з тіла (`PushEvent.RepositoryUrls`) через `GitUrl.Key`; не знайдено — `Ignored`.
- **Перевірка.** Запит автентифікує сам секрет. Тіло читається сирим (до 5 MiB, інакше 413), бо підпис покриває точні байти.
  - Провайдери — ієрархія `GitWebhookProvider` → `HmacWebhookProvider` → `GitHubWebhook`, `GiteaWebhook` (також Forgejo); `GitLabWebhook` порівнює `X-Gitlab-Token` з секретом. Gitea надсилає ще й заголовки GitHub, тому її провайдер питається першим.
  - Усі порівняння — `CryptographicOperations.FixedTimeEquals` над SHA-256 обох значень (не видає й довжини).
  - Невідомий репозиторій, репозиторій без секрету, невідомий відправник, неправильний підпис і нечитабельний секрет — однаковий 401 (`WebhookRejectedException`); тіло не JSON — 400 лише після перевірки підпису. Ні тіло, ні підпис не логуються.
- **Деплой:** `Deployment.QueueFromWebhook` (тригер `Webhook`, без користувача, `pull: missing`) для стеків із тією самою гілкою й `DeployOnPush`. Деплой бере голову гілки, а не `after` з тіла: повторно надіслана стара доставка передеплоїть поточну голову, а не відкотить стек.
- **Відповідь** (`WebhookResponse`) потрібна для журналу доставок на git-сервері:

  | Результат | Коли |
  |---|---|
  | `Pong` | `ping` GitHub |
  | `Ignored` | інші події, теги, видалена гілка, репозиторій не знайдено |
  | `NotAllowed` | підключення більше не дозволене в середовищі стеку |
  | `AlreadyDeployed` | стек уже працює на коміті `after` (повторна доставка) |
  | `AgentOffline` | агент офлайн, нічого не ставиться (інваріант 7) |
  | `AlreadyQueued` | деплой у черзі: статус `Running` зберігається до fetch, тож він ще візьме цей push |
  | `AlreadyRunning` | деплой, що вже йде, має той самий коміт |
  | `AfterRunning` | деплой іде з іншим комітом: після нього `PushFollowUpObserver` поставить новий, якщо агент онлайн і ніхто не поставив свій |

- **Деплой після поточного:** як `PushDeploymentGate` не губить push, описано в його коментарі. Після рестарту ЦА push треба надіслати повторно з git-сервера (Redeliver).

## Хук деплою

- **Токен:** на стеку, зберігається лише хеш (`Stack.DeployHookTokenHash`, `SecretToken.Hash`, перевірка `AcceptsDeployHook` за сталий час). Створює й видаляє Editor стеку (`POST|DELETE /api/stacks/{id}/deploy-hook`, `StackService`), для системного стеку — 409.
- **Виклик:** `DeployHookService`, тіло ігнорується: `Deployment.QueueFromDeployHook` (тригер `DeployHook`, без користувача, голова гілки), 202 з `Queued`. Токен передається в заголовку, бо адресу зберігають логи проксі й CI.
- **Під час іншого деплою:** той, що в черзі й так тягне всі образи, — `AlreadyQueued`; інший незавершений — `AfterRunning`. `PushDeploymentGate` пам'ятає політику pull: `Always` хука не перекривається пізнішим push, а `PushFollowUpObserver` ставить `QueueFromDeployHook` і, якщо між ними хтось поставив деплой без pull, чекає й за ним.
- **Відмови:** невідомий стек, стек без хука, немає чи неправильний токен — однаковий 401 (`WebhookRejectedException`); офлайн-агент — 503, нічого не ставиться.

## Хук образів

Хук образів стеку (ADR-0015) просить ЦА перевірити політики образів зараз або оновити образ до конкретного тегу; коміт і деплой робить сама ЦА. Токен — `Stack.ImageHookTokenHash`, як у хука деплою, але окремий: хук деплою перевдеплоює голову гілки, хук образів змінює тег у межах політики. Деталі — в [image-updates.md](image-updates.md).
