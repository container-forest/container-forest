# ADR-0001. Структура репозиторію і шари ЦА

- Статус: прийнято; п. 4 замінено [ADR-0002](0002-proto-contracts-layout.md)
- Дата: 2026-09-29

## Контекст

У README структура репозиторію була позначена як «не затверджено». Перед тим як писати код, потрібно було вирішити:

- як розкласти ЦА на проєкти;
- де лежать контракти і чи потрібен спільний проєкт (SharedKernel);
- як вебпанель отримує типи HTTP API;
- де лежать фронтенд і тести.

## Рішення

```
ContainerForest.slnx
Directory.Build.props         спільні властивості .NET-проєктів
Directory.Packages.props      центральне керування версіями NuGet
global.json
src/
  Contracts/                  лише .proto (див. ADR-0002)
  Aggregator/
    Domain/                   ContainerForest.Aggregator.Domain
    Application/              ContainerForest.Aggregator.Application
    Infrastructure/           ContainerForest.Aggregator.Infrastructure
    Api/                      ContainerForest.Aggregator.Api (composition root)
  Agent/                      ContainerForest.Agent
  DiscordBot/                 ContainerForest.DiscordBot
  Web/                        Vite + React + TS
tests/                        дзеркалить src/, проєкти створюються з першим тестом
deploy/
docs/
```

1. **Одна папка верхнього рівня в `src/` відповідає одному окремо розгортаному застосунку.** Виняток — `Contracts` зі спільними `.proto`-файлами. Групи на кшталт `Clients/` не вводимо, бо в них немає спільної збірки, коду чи способу розгортання.
2. **ЦА ділиться на шари.** Залежності йдуть лише всередину: `Domain ← Application ← Infrastructure ← Api`.
   - `Domain` не має залежностей.
   - `Application` містить сценарії, команди, декоратор авторизації, `DeployPipeline`, `IDeployStep` і порти, наприклад канал до агента. На `Contracts` воно не посилається.
   - `Infrastructure` містить EF Core, LibGit2Sharp, виклик SOPS, `AgentRegistry` і маппінг доменних команд у proto-повідомлення.
   - `Api` містить REST, SignalR, gRPC-сервіси, фонові сервіси та DI.
3. **SharedKernel немає.** Домен є лише в ЦА. Агент і бот ділять із ЦА тільки контракти. Агент перевіряє назву стеку й шляхи файлів самостійно, як другий рубіж захисту, тож дубль цих кількох рядків свідомий.
4. **`Contracts` містить лише `.proto`.** Каталоги повторюють назву пакета: `Protos/container_forest/<модуль>/v<N>/`. Код генерується через `Grpc.Tools` з `GrpcServices="Both"`, щоб усі споживачі мали одну збірку.
5. **HTTP DTO живуть в `Api`, вебпанель отримує їх через OpenAPI.**
   - `Microsoft.AspNetCore.OpenApi` описує API.
   - `Microsoft.Extensions.ApiDescription.Server` під час збірки пише документ у `src/Aggregator/Api/openapi/`. Документ комітиться, тож зміни контракту видно в диффі PR.
   - `src/Web` генерує з нього типи (`openapi-typescript`) і клієнт (`openapi-fetch`).
   - Повідомлення SignalR OpenAPI не описує. Для MVP їхні TS-типи пишуться вручну.
6. **Назва ЦА в коді — `Aggregator`**, як в іменах Docker-образів і в документації.
7. **Збірка.**
   - `TargetFramework`, `Nullable` і `ImplicitUsings` задаються один раз у `Directory.Build.props`.
   - Версії пакетів задаються лише в `Directory.Packages.props`.
   - Попередження стають помилками тільки в CI (`CI=true`), щоб не заважати локальній роботі.

## Наслідки

- Реалізації кроків пайплайну розходяться по шарах. Інтерфейс і `DeployPipeline` лежать в `Application`, а кроки із зовнішніми залежностями (`GitFetchStep`, `SopsDecryptStep`) — в `Infrastructure`.
- Щоб згенерувати OpenAPI-документ, збірка `Api` запускає застосунок. Тому старт не має вимагати зовнішніх ресурсів. Якщо конфігурація з `ValidateOnStart()` або БД цьому заважатиме, ці перевірки треба буде вимикати для генерації документа.
- Змінив DTO — перезбери `Api`, закомітить оновлений `openapi/*.json` і перегенеруй клієнт у `src/Web`.

## Відкинуті варіанти

- **SharedKernel.** Спільного коду, крім контрактів, немає, а порожній проєкт «на виріст» згодом збирає все підряд.
- **HTTP DTO у `Contracts`.** Їх не використовує жоден .NET-споживач, крім ЦА.
- **`web/` у корені репозиторію.** Технічно рівноцінний варіант. Обрано `src/Web`, щоб весь код застосунків лежав у `src/`.
- **Окремі проєкти Presentation і Host для ЦА.** Для такого розміру системи це зайвий рівень.
