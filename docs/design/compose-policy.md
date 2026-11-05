# Політика compose й адміністратор середовища

Правила, винятки й обґрунтування — в [ADR-0012](../adr/0012-stack-isolation.md), короткий опис для користувача — в [architecture.md](../architecture.md#політика-compose). Тут — де це в коді й деталі, яких там немає. Шляхи бекенду відносно `src/Aggregator`, панелі — відносно `src/Web`.

Код: `Domain/Policies`, `Application/Policies`, `Application/Deployments/Steps/ComposePolicyStep.cs`, `Api/Contracts/PolicyContracts.cs`.

## Адміністратор середовища

- `Role.EnvironmentAdmin` (4): `RoleAssignment.ForStack` і `ChangeRole` на стеку відмовляють. Право — `Permission.AdministerEnvironment`.
- `EnvironmentAdministration.GetAsync` (невидиме середовище — 404) замість `GetAdministratorAsync` у `EnvironmentService` (зміна, токен реєстрації, відкликання агента), `AgeKeyService`, `RegistryCredentialService`, `ComposePolicyService.ChangeAsync`, `StackService.SetPolicyAllowancesAsync`.
- Ролі на середовищі — `EnvironmentRoleService`. У відповідях: `EnvironmentResponse.canAdminister`, `StackResponse.canAdministerEnvironment`.

## Модель

- `ComposePolicy` (таблиця `ComposePolicies`, ключ — `EnvironmentId`; немає рядка — `Default`): списки — JSON-колонки (`JsonListStorage`). Типовий `DataRoot` не лежить у захищеному каталозі.
- `Stack.PolicyAllowances`, `Stack.DeployedComposeFacts` (пише `Stack.RecordDeployment(..., composeFacts)`), `Stack.PolicyUnrestricted`.
- `PolicyAllowance` нормалізує рядок (`capability:cap_sys_admin` → `capability:SYS_ADMIN`, шлях без `/` у кінці). `Covers` — префікс для шляхів, `path-rw` покриває й читання; `CoversExactly` — для захищених шляхів. `HostPath` порівнює за сегментами.
- `ComposePolicyEvaluator.Evaluate` повертає порушення з винятком або без нього (нічим не знімається: диски, вкладення в rw-джерело, посилання, невидимий агенту шлях) і попередження. `KnownServiceKeys` — allowlist ключів сервісу. `ComposeFactsReader` розбирає модель compose у `ComposeFacts`.
- Ім'я контейнера — `ComposePolicyEvaluator.ContainerNameOwner`, перевірка при створенні — у `StackService.CreateAsync`.

## Пайплайн

- `ValidateComposeStep`: парсер посилань — `ComposeService.ReferencedFiles`, `ComposeFile.IncludedFiles`, `HasTags`.
- `ComposePolicyStep`: політику `DeploymentRunner` завантажує в `DeployContext.Policy`, рендер — `IAgentChannel.RenderStackAsync` (`Deployments:RenderTimeout`). Засвідчення для винятку: `Endorsement` Signed → `SignedBy`, ApprovedUnsigned → `RequestedBy`.
- `VerifyPaths` — лише в Enforce: джерела, крім системних шляхів, куди може писати контейнер (`IsContainerWritable`). Без винятку для засвідченого вмісту й стеку без обмежень.

## Агент

- `RenderStackHandler`, `ComposeInvocation.Config`, `IComposeRunner.CaptureAsync` (stderr — прогрес), `RenderedModel` (що викидається з моделі — в ADR).
- `HostPathInspector` шукає посилання по компонентах шляху через `Agent:HostRoot`, без нього — лише у власних каталогах агента. Посилання, яке агент сам кладе в каталог стеку на місці секретного файлу пакета (`HostPathInspector.SecretLinks`), пропускається, лише якщо веде саме в tmpfs цього файлу.
- Тести зі справжнім compose (`RealComposeRenderTests`) у CI обов'язкові (`CF_TEST_REQUIRE_DOCKER_COMPOSE=1`).

## Панель і ключі стеку

- Винятки — `AllowanceList` і діалог (`src/components/compose-policy/allowance-list.tsx`), каталог видів — `allowanceKinds` у `src/features/compose-policy/model.ts`. «Дозволити» біля порушень у звіті: `StackPolicyReportResponse.allowances`, `PolicyViolationResponse.stackOnly`.
- Ключі стеку: `AgeKey.StackId`, `AgeKey.Serves(stack)`, для git-фільтра — `SopsSecretsService.EditableAsync`, створення — `POST /api/environments/{id}/age-keys?stackId=`.
