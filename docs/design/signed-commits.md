# Підписи комітів

Правила, тригери й обґрунтування — в [ADR-0011](../adr/0011-signed-commits.md). Тут — де це в коді й деталі, яких там немає. Шляхи відносно `src/Aggregator`.

- **Код:** `Domain/Access/SigningKey.cs`, `SshKeys.cs`, `Application/Deployments/Steps/VerifyCommitStep.cs`, `SigningKeyService`, `Api/Contracts/SigningKeyContracts.cs`.
- **`SigningKey`:** `SshPublicKey.Parse` перевіряє структуру блоба. Область — `IsRestricted` + середовища, `Scope` — `AccessScope` без лише-читання. `AddedBy` — хто додав, `AddedByAdministrator` показується поруч із підписом. Аудит: `SigningKeyAdded/Changed/Removed` (розширення області — з позначкою), `StackEndorsementChanged`.
- **Опції стеку:** `PUT /api/stacks/{id}/require-signed-commits|require-image-digests`, для системного стеку — 409.
- **`VerifyCommitStep`**, за порядком:
  1. `ICommitInspector.ReadSignatureAsync`. Підписані дані — UTF-8 рядок, тож коміт в іншому кодуванні просто не перевіриться.
  2. `SshSignature.TryParse` дає ключ для пошуку за відбитком.
  3. `ICommitSignatureVerifier` (`SshKeygenSignatureVerifier`) запускає `ssh-keygen` через `CliProcess`.
  4. Права: `AccessPolicy.CanPerform(new Actor(owner, key.Scope), …)`.
  5. Автоматичні тригери (`Deployment.IsAutomatic`): `ICommitInspector.DescendsFromAsync` від `DeployedCommit`, інакше `NotDescendant`.
  6. Rehydrate коміту, рівного `DeployedCommit`, — `AlreadyDeployed` без перевірки.
- **Непідписаний коміт:** `DeployRequest.approveCommit` → `Deployment.ApprovedCommit`; результат — `ApprovedUnsigned` (причина зберігається) або `ApprovalOutdated`.
- **Коміти оновлень образів** (ADR-0015): перевірка підпису винесена в `CommitEndorsementService`, який для непідписаного коміту чи коміту з невідомим ключем шукає `ImageUpdateRecord` і перевіряє ланцюжок до засвідченого кореня. Результат — `CommitEndorsement.ImageUpdate` або `EndorsementProblem.ImageUpdateMismatch`, деталі — в [image-updates.md](image-updates.md#засвідчення).
- **Результат у деплої:** `Deployment.Endorsement`, `EndorsementProblem`, `SignedBy` + `SignerName` (без FK), `SigningKeyFingerprint`; у відповідях — `EndorsementResponse`.
- **Тести:** зі справжнім `ssh-keygen` (`LibGit2SharpGitSourceTests`) без нього пропускаються, у CI (`CF_TEST_REQUIRE_SSH_KEYGEN=1`) падають; API-тести підміняють перевірку `StubCommits`.
