# ADR-0003. Secrets in the aggregator's database are encrypted with ASP.NET Core Data Protection

- Status: accepted
- Date: 2026-09-30

## Context

The aggregator has to store values it needs in plain text: container registry passwords and tokens (the agent receives them to pull images), and later git repository deploy tokens and private SOPS age keys. They can't be hashed like agent credentials, so reversible encryption is needed. CLAUDE.md left the choice open: Data Protection, or our own master key from configuration.

Options:

1. **ASP.NET Core Data Protection** with a key ring in a directory on the aggregator's disk. Authenticated encryption, key rotation (every 90 days by default; old keys stay for decryption), a separate key per purpose through a purpose string. The `Microsoft.AspNetCore.DataProtection` package is part of the ASP.NET Core platform.
2. **A master key from configuration** (`CF_SECRETS__MASTERKEY`) and AES-256-GCM by hand. The key could be kept apart from the database, for example in deployment secrets, but the ciphertext format, key versions and rotation would all have to be written and tested by us.

## Decision

1. Data Protection is used. The `ISecretProtector` port in `Application` (`Protect`/`Unprotect` with a `SecretPurpose`) is implemented by `DataProtectionSecretProtector` in `Infrastructure`. `Domain` sees only the encrypted value (`RegistryCredential.ProtectedPassword`, `GitRepository.ProtectedAccessToken`; since ADR-0006, `GitConnection.ProtectedAccessToken`).
2. Each kind of secret has its own purpose (`ContainerForest.Secrets.<SecretPurpose>`), so a registry password won't decrypt as a git token.
3. The key ring lives in `DataProtection:KeyDirectory` (`CF_DATAPROTECTION__KEYDIRECTORY`), `data-protection-keys` in the aggregator's working directory by default. `ApplicationName` is `container-forest`.
4. A value that can't be decrypted (a lost key ring, a corrupted record) gives a `SecretUnreadableException`. The pipeline step tells the user the credentials have to be entered again, without revealing details.
5. While generating the OpenAPI document (the host starts during the build), an ephemeral provider is used so the build doesn't create a key ring.

## Consequences

- **Losing the key ring means losing every stored secret.** Back up the key directory together with the database, and mount it into the aggregator's container as a volume.
- On Linux the keys on disk aren't encrypted; that's a documented Data Protection mode. The protection is the directory's permissions (the aggregator's user only). Whoever reads both the database and the key ring reads the secrets. That's the same level as a master key next to the database; encrypting the key ring with a certificate (`ProtectKeysWithCertificate`) can be added later without changing the data format.
- No new dependency: the package is part of ASP.NET Core.
- Git tokens and age keys will use the same `ISecretProtector` with new `SecretPurpose` values.
