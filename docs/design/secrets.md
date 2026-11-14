# Secrets: SOPS, the git filter, rehydrate

Decisions: [ADR-0003](../adr/0003-secrets-at-rest.md), [ADR-0010](../adr/0010-git-secrets-filter.md). How the agent writes secrets: [src/Agent/CLAUDE.md](../../src/Agent/CLAUDE.md). Aggregator paths are relative to `src/Aggregator`.

## Keys

- **Model:** `AgeKey` (table `AgeKeys`), an X25519 age key pair per environment or stack (ADR-0012). The private part is encrypted with `ISecretProtector` with `SecretPurpose.AgeIdentity`; responses carry only the `recipient`.
- **Rotation:** an environment can have several keys: the new one encrypts new files, and all of them decrypt.
- **Access** (`AgeKeyService`): an administrator or the environment's administrator creates and deletes keys. Everyone who sees the environment sees the public keys: `.sops.yaml` needs them.
- **Generation:** `age-keygen` (`AgeKeygenCli`) creates the pair; its output is parsed and never logged.

## Decrypting during a deployment

`SopsDecryptStep` runs right after `GitFetchStep`.

- **What counts as encrypted:** only a file with sops metadata in its own format (`SopsFile.IsEncrypted`), since a file that merely mentions sops or `ENC[` must deploy as it is. The format is chosen by extension, as sops itself does:

  | Format | Marker |
  |---|---|
  | YAML | a top-level `sops:` key |
  | JSON | a `sops` object |
  | dotenv | `sops_version=` |
  | INI | `[sops]` |
  | anything else (binary) | `{"data", "sops"}` |

- **Keys:** only the target environment's. No key, someone else's key or an unreadable private key is a deployment error with a hint.
- **Result:** the file goes to the agent with `StackFile.secret = true` (tmpfs + a symbolic link); the deployment log shows only paths.
- **`.gitattributes`:** the step checks them first (`GitAttributes`, with `.gitignore` rules for the `filter` attribute only): a file under `filter=forest` without sops metadata is a deployment error. `LibGit2SharpGitSource` puts the `.gitattributes` files of directories above the stack into `GitSnapshot.OuterAttributes`.

## Calling sops

- **`SopsCli`** runs `sops decrypt --filename-override <path> /dev/stdin`: the ciphertext goes through stdin and the plain text through stdout; nothing is written to the aggregator's disk.
- **Environment:** keys are passed in `SOPS_AGE_KEY` (separated by `\n`). `CliProcess` clears the child process's environment (keeping `PATH`), so the aggregator's `CF_*` never reach sops.
- **Errors:** exit code 128 (`CouldNotRetrieveKey`) means "none of the environment's keys is a recipient"; anything else is stderr's first line (sops prints no plain text when decrypting).
- **Binaries:** `scripts/install-sops.sh` installs `sops` and `age-keygen` into the aggregator's image (versions and SHA-256 for amd64/arm64 are in the script; update them there) in a separate Dockerfile stage. Paths and the timeout: `Sops:SopsPath`, `Sops:AgeKeygenPath`, `Sops:Timeout`.
- **Tests:** `SopsToolsTests` with the real tools are skipped without them and fail in CI (`CF_TEST_REQUIRE_SOPS=1`; the same script installs the tools).

## The API for the git filter

`SopsSecretsService`, `Api/Contracts/SopsContracts.cs`, ADR-0010. Both endpoints accept up to 1 MiB of content and have a per-user `sops` rate limit (a token bucket: 300, +60 per 10 s).

**`POST /api/sops/decrypt`**

- **Permission:** `AccessScope.CanDecryptSecrets`. A session has it; a token only with the `secrets` flag (claim `cf:secrets`, never together with read-only).
- **Keys:** those whose `recipient` is in the file's metadata (`SopsFile.AgeRecipients`, only a hint) and whose environment is in the actor's `AccessPolicy.EditorEnvironments`. An unrestricted administrator gets all of them.
- **Refusals:** no aggregator key among the recipients is a `SopsDecryptionException` (422); only other people's environments is a 403.
- **Log:** who and which path, no content.

**`POST /api/sops/encrypt`**

- **Permission:** any actor except a read-only one.
- **Rule:** `YamlSopsConfigReader` picks the first `.sops.yaml` rule by `path_regex` (the path from the repository root, `RegexOptions.NonBacktracking` in place of RE2) and accepts only `age` and the "what to encrypt" parameters. Another key type is a `SopsConfigException` (400).
- **Running:** `SopsCli.EncryptAsync` writes a temporary JSON config with only that rule and runs `sops --config <file> encrypt`. The repository's `.sops.yaml` never reaches sops, so the aggregator never calls KMS, Vault or PGP.
- **Errors:** a sops error is a 422, and only the exit code is logged, since stderr may quote plain text.

## The git filter in the CLI

`src/Cli/Secrets`, ADR-0010.

**Setup**

- `forest git setup` writes `filter.forest.process` (`forest git filter-process`, the binary's absolute path in single quotes if it's `forest` itself), `filter.forest.required`, `diff.forest.textconv` and `forest.context` to the clone's local config. It also installs a pre-commit hook (`forest secrets check --staged`; it doesn't overwrite someone else's hook) and checks out unchanged encrypted files again under the filter.
- `git teardown` (`GitFilterSetup.Remove`) is the opposite of `setup`: it removes the `filter.forest` and `diff.forest` sections, the `forest.*` keys, its hook and `.git/forest`, and puts the ciphertext back into the working files. Uncommitted changes mean a refusal.

**The filter itself**

- `GitFilterProcess` implements the pkt-line protocol version 2 (`PktLineReader`/`PktLineWriter`) with the clean and smudge capabilities.
- Each file is a `SmudgeOperation` or a `CleanOperation` (Template Method over `FilterOperation`); backends are `ISopsBackend` (`AggregatorSops` through `ForestApi`, `LocalSops` with `forest.localSops`).
- Smudge without access returns the ciphertext and success (the reason goes to stderr once); clean returns `status=error` on any error.
- `FilterCache` (`.git/forest/cache.json`, HMAC key `.git/forest/key`, 0600) gives unchanged plain text the same ciphertext back. `secrets updatekeys` forgets the entries and runs `git add --renormalize`.

**`secrets remove`** takes the filter off a file.

- It deletes its own `.gitattributes` line, or for a pattern appends `/<path> -filter -diff`. If a deeper `.gitattributes` still turns the filter on, it rolls back and refuses.
- By default the clone gets the ciphertext from git back (uncommitted changes mean a refusal); `--plain --yes` stages the plain text.

**`secrets rule list|add|remove`** edits `.sops.yaml` through `SopsConfigFile`.

- It works with the lines of the block list `creation_rules`: a comment before a rule belongs to it, and unchanged rules are written as they were. Another shape (flow style, block scalars, key lists) means a refusal.
- A new rule goes before the first one it overlaps, with the keys common to all rules that aren't any environment's keys (backup keys).

**Line endings.** Git converts them before smudge, so:

- `.gitattributes` lines are written as `filter=forest diff=forest eol=lf` (`SecretsFiles.FilterAttributes`; old ones without `eol` are `LegacyFilterAttributes`, and `AddAttributes` appends `eol=lf` to them).
- `secrets fix-eol` does the same for the root `.gitattributes`, stages ciphertext without CR through `hash-object` + `update-index` (not `git add`, which would also take uncommitted changes to the plain text) and rereads the files.
- `GitFilterSetup.Refresh` also rereads ciphertext that differs from git's only by CR, and returns the checkout's stderr (the reason from the filter).
- `setup`, `status` and `check` warn about rules without `eol=lf`; `check` rejects ciphertext with CRLF.
- Clean strips CR from dotenv and INI (`SopsFiles.PlainForSops`) before caching and encrypting. The aggregator doesn't normalise ciphertext; it only names CRLF in the error (`SopsCli`).

## Rehydrate

File secrets live in tmpfs and disappear when the host reboots. Docker starts containers before the agent connects, so containers with file secrets fail until rehydrate. The aggregator's signal to send the secrets again is a new `boot_id` in `Hello`.

- **Start:** `DockerEnvironment.RecordConnection` compares the `boot_id` with the previous one. If it's new, `AgentGatewayService` calls `SecretRehydrationService` after `registry.Open` (so deployments go to this agent).
- **Which stacks:** it queues `Deployment.QueueRehydrate` for every stack with `Stack.NeedsRehydration`: there's a `DeployedCommit`, the last successful deployment had secret files (`DeployedWithSecrets`, from `DeployContext.HasSecretFiles`), and the stack isn't a system stack. A stack with an unfinished deployment is skipped: that deployment will send the secrets itself.
- **No action:** the first `Hello` after enrollment (`LastBootId` = null) and a reconnect without a reboot deploy nothing. A failure to queue is only logged; the connection doesn't suffer.
- **A pinned commit** (`IsPinned`, trigger `Rehydrate`, no user, `pull: missing`): the branch may have moved on, and restoring secrets mustn't deploy anything new. `GitFetchStep` still fetches the branch (the cache may be gone) but reads the pinned commit; an unreachable git server is only a warning, and the commit comes from the aggregator's cache.
- **The result** goes to the deployment history and notifications like an ordinary deployment.
- **Caveat:** the stack's source (`ChangeSource`) after the last deployment isn't tracked, and rehydrate reads the pinned commit through the current source.
