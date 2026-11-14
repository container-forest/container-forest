# Signed commits

Rules, triggers and reasoning are in [ADR-0011](../adr/0011-signed-commits.md). This note covers where it lives in the code and details the ADR doesn't. Paths are relative to `src/Aggregator`.

- **Code:** `Domain/Access/SigningKey.cs`, `SshKeys.cs`, `Application/Deployments/Steps/VerifyCommitStep.cs`, `SigningKeyService`, `Api/Contracts/SigningKeyContracts.cs`.
- **`SigningKey`:** `SshPublicKey.Parse` checks the blob's structure. The scope is `IsRestricted` plus environments; `Scope` is an `AccessScope` without read-only. `AddedBy` is who added it; `AddedByAdministrator` is shown next to the signature. Audit: `SigningKeyAdded/Changed/Removed` (widening the scope is flagged), `StackEndorsementChanged`.
- **Stack options:** `PUT /api/stacks/{id}/require-signed-commits|require-image-digests`; 409 for a system stack.
- **`VerifyCommitStep`**, in order:
  1. `ICommitInspector.ReadSignatureAsync`. The signed data is a UTF-8 string, so a commit in another encoding simply fails verification.
  2. `SshSignature.TryParse` gives the key to look up by fingerprint.
  3. `ICommitSignatureVerifier` (`SshKeygenSignatureVerifier`) runs `ssh-keygen` through `CliProcess`.
  4. Permissions: `AccessPolicy.CanPerform(new Actor(owner, key.Scope), …)`.
  5. Automatic triggers (`Deployment.IsAutomatic`): `ICommitInspector.DescendsFromAsync` from `DeployedCommit`, otherwise `NotDescendant`.
  6. Rehydrating a commit equal to `DeployedCommit` is `AlreadyDeployed` without verification.
- **An unsigned commit:** `DeployRequest.approveCommit` → `Deployment.ApprovedCommit`; the result is `ApprovedUnsigned` (the reason is kept) or `ApprovalOutdated`.
- **Image update commits** (ADR-0015): signature checks live in `CommitEndorsementService`, which, for an unsigned commit or one signed with an unknown key, looks up the `ImageUpdateRecord` and verifies the chain to an endorsed root. The result is `CommitEndorsement.ImageUpdate` or `EndorsementProblem.ImageUpdateMismatch`; details in [image-updates.md](image-updates.md#endorsement).
- **The result in a deployment:** `Deployment.Endorsement`, `EndorsementProblem`, `SignedBy` + `SignerName` (no FK), `SigningKeyFingerprint`; in responses, `EndorsementResponse`.
- **Tests:** those with a real `ssh-keygen` (`LibGit2SharpGitSourceTests`) are skipped without it and fail in CI (`CF_TEST_REQUIRE_SSH_KEYGEN=1`); API tests replace verification with `StubCommits`.
