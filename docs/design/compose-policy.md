# Compose policy and environment administrators

Rules, allowances and reasoning are in [ADR-0012](../adr/0012-stack-isolation.md), a short description for users in [architecture.md](../architecture.md#compose-policy). This note covers where it lives in the code and details the ADR doesn't. Backend paths are relative to `src/Aggregator`, panel paths to `src/Web`.

Code: `Domain/Policies`, `Application/Policies`, `Application/Deployments/Steps/ComposePolicyStep.cs`, `Api/Contracts/PolicyContracts.cs`.

## Environment administrator

- `Role.EnvironmentAdmin` (4): `RoleAssignment.ForStack` and `ChangeRole` on a stack refuse it. The permission is `Permission.AdministerEnvironment`.
- `EnvironmentAdministration.GetAsync` (an invisible environment is a 404) replaces `GetAdministratorAsync` in `EnvironmentService` (changes, enrollment token, revoking the agent), `AgeKeyService`, `RegistryCredentialService`, `ComposePolicyService.ChangeAsync` and `StackService.SetPolicyAllowancesAsync`.
- Environment roles: `EnvironmentRoleService`. In responses: `EnvironmentResponse.canAdminister`, `StackResponse.canAdministerEnvironment`.

## Model

- `ComposePolicy` (table `ComposePolicies`, key `EnvironmentId`; no row means `Default`): lists are JSON columns (`JsonListStorage`). The default `DataRoot` isn't inside a protected directory.
- `Stack.PolicyAllowances`, `Stack.DeployedComposeFacts` (written by `Stack.RecordDeployment(..., composeFacts)`), `Stack.PolicyUnrestricted`.
- `PolicyAllowance` normalises the string (`capability:cap_sys_admin` → `capability:SYS_ADMIN`, a path without a trailing `/`). `Covers` is a prefix match for paths, and `path-rw` covers reading too; `CoversExactly` is for protected paths. `HostPath` compares by segments.
- `ComposePolicyEvaluator.Evaluate` returns violations, with or without a possible allowance (some can't be lifted at all: devices, nesting into a read-write source, links, a path the agent can't see), and warnings. `KnownServiceKeys` is the allowlist of service keys. `ComposeFactsReader` turns the compose model into `ComposeFacts`.
- Container names: `ComposePolicyEvaluator.ContainerNameOwner`; the check at creation is in `StackService.CreateAsync`.

## Pipeline

- `ValidateComposeStep`: the reference parser is `ComposeService.ReferencedFiles`, `ComposeFile.IncludedFiles`, `HasTags`.
- `ComposePolicyStep`: `DeploymentRunner` loads the policy into `DeployContext.Policy`; rendering is `IAgentChannel.RenderStackAsync` (`Deployments:RenderTimeout`). Endorsement for an allowance: `Endorsement` Signed → `SignedBy`, ApprovedUnsigned → `RequestedBy`.
- `VerifyPaths` runs only in Enforce: sources a container can write to (`IsContainerWritable`), except system paths. No exception for endorsed content or an unrestricted stack.

## Agent

- `RenderStackHandler`, `ComposeInvocation.Config`, `IComposeRunner.CaptureAsync` (stderr is progress), `RenderedModel` (what's dropped from the model is in the ADR).
- `HostPathInspector` looks for links along the path's components through `Agent:HostRoot`; without it, only in the agent's own directories. A link the agent itself puts in the stack directory in place of a bundle's secret file (`HostPathInspector.SecretLinks`) is skipped only if it points exactly at that file's tmpfs copy.
- Tests with a real compose (`RealComposeRenderTests`) are required in CI (`CF_TEST_REQUIRE_DOCKER_COMPOSE=1`).

## Panel and stack keys

- Allowances: `AllowanceList` and its dialog (`src/components/compose-policy/allowance-list.tsx`); the catalogue of kinds is `allowanceKinds` in `src/features/compose-policy/model.ts`. *Allow* next to violations in the report: `StackPolicyReportResponse.allowances`, `PolicyViolationResponse.stackOnly`.
- Stack keys: `AgeKey.StackId`, `AgeKey.Serves(stack)`; for the git filter, `SopsSecretsService.EditableAsync`; creation is `POST /api/environments/{id}/age-keys?stackId=`.
