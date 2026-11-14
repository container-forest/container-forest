# ADR-0010. A git filter for SOPS secrets

- Status: accepted
- Date: 2026-10-10

## Context

A stack's secrets live in the repository encrypted with SOPS to the environment's age key, and the aggregator decrypts them during a deployment. The environments' private keys exist only in the aggregator (invariant 1). So a person editing a secret needs their own age key in `.sops.yaml` and a local `sops`: decrypt, change, encrypt, commit. In practice that means personal keys spreading across laptops, manual steps that are easy to skip (and commit plain text), and `sops updatekeys` after every change in the team.

Options considered:

1. **The aggregator as a git server ("managed repo")**: push and pull go through the aggregator, which encrypts and decrypts on the fly. Rejected: git addresses content by hash, so every user would see a different commit of the same tree; the aggregator would have to be a full git server (protocols, branch permissions, LFS, hooks), and the repository would no longer live where the team already works (GitHub, Gitea, GitLab).
2. **A separate key per stack.** Rejected for now: the isolation boundary is the host. A stack's Editor can mount anything from the host into a container, including other stacks' directories and tmpfs, so a separate key protects nothing until compose files are restricted by a policy (privileged, `cap_add`, bind mounts outside the stack directory, `docker.sock`, `env_file` outside the stack). This can be revisited together with such a policy in `ValidateComposeStep`.
3. **A client-side git filter** (chosen): git itself calls the filter on checkout (smudge) and on add (clean). The filter asks the aggregator to decrypt a file with the keys of environments where the person has the right, and to encrypt it for the rules of `.sops.yaml`. The repository keeps ordinary sops files, and the git server can be anything.

## Decision

1. **The format doesn't change.** Git holds standard sops files; both the deployment (`SopsDecryptStep`) and `sops decrypt` with any recipient key decrypt them. The filter is a layer on top of the existing SOPS workflow, not a replacement: manual `sops` with your own key keeps working, and the repository isn't tied to container-forest.

2. **Which files.** `.gitattributes` marks them `filter=forest diff=forest`. `forest git setup` writes to the clone's local config:
   - `filter.forest.process = forest git filter-process`: one process per git command, the pkt-line protocol ("Long Running Filter Process" in gitattributes(5)), with the clean and smudge capabilities;
   - `filter.forest.required = true`: a filter error stops git rather than skipping the file;
   - `diff.forest.textconv = forest git textconv` with `cachetextconv = false`: `git diff` shows plain text.

   Git silently ignores a filter driver it doesn't know, so a clone without `forest git setup` sees and commits the files as they are.

3. **Smudge** (checkout): an encrypted file is decrypted through the aggregator (`POST /api/sops/decrypt`). Without permission, with the aggregator unreachable or without a sign-in, the file stays encrypted, the reason goes to stderr once, and the checkout continues. A person without access gets a working clone, just without plain-text secrets.

4. **Clean** (add, status, diff): plain text is encrypted through the aggregator (`POST /api/sops/encrypt` with the clone's `.sops.yaml`). An already encrypted file passes unchanged. Any error is a git error: plain text never reaches git.
   - sops encrypts with a new data key every time, so without memory every `git status` would see every secret as changed. The cache `.git/forest/cache.json` remembers, per path, the ciphertext and an HMAC-SHA256 of the plain text with a random per-clone key (`.git/forest/key`, owner only). Unchanged text gets the same ciphertext back. The cache holds no plain text.

5. **Decryption permissions.** The aggregator tries only the keys of environments where the actor is an Editor (of the environment or at least one of its stacks, `AccessPolicy.EditorEnvironments`). An administrator without token restrictions can decrypt everything.
   - The recipients from the file's metadata are only a hint about which keys to try: anyone can write them, so they don't widen access.
   - There's no binding of a repository to an environment: everything the client says about a file, except the ciphertext itself, is unverifiable. The restriction is cryptographic: a file encrypted only for prod is decrypted only by prod's key.
   - This is no wider than existing rights: an Editor of an environment or a stack can deploy this file and read it on the host, since they're effectively root.
   - A file none of whose recipients is an environment key is a 422; a file of other people's environments is a 403.

6. **The `secrets` flag on an API token** (an addition to ADR-0007). Tokens live in files and CI, so only a token created with this right can decrypt (`ApiToken.CanDecryptSecrets`, claim `cf:secrets`, `AccessScope.CanDecryptSecrets`). A panel session always can.
   - The flag is off by default. A read-only token can't have it (400).
   - It's turned on with the *Decrypt secrets* checkbox in the new token dialog and on the CLI sign-in confirmation page. `forest login --secrets` only pre-checks the box; the person decides.
   - The token's environment restriction applies as always: a token for staging won't decrypt a prod file, even if its owner is a prod Editor.

7. **Encryption in the aggregator.** Encrypting needs no private key, so any actor except a read-only token can encrypt.
   - The aggregator itself picks the first `.sops.yaml` rule whose `path_regex` matches the path from the repository root (sops's RE2 ≈ .NET's non-backtracking engine, linear time).
   - Then the aggregator runs `sops` with a config built only from that rule's age recipients and "what to encrypt" parameters. A rule with KMS, Vault, PGP or `key_groups` is rejected (400): otherwise a repository could make the aggregator contact services it names (SSRF) or run gpg. A local `sops` encrypts such files.

8. **A local sops** (`forest git setup --local-sops`, `forest.localSops = true`). Smudge first tries `sops` with the person's keys (`SOPS_AGE_KEY_FILE`, etc.), then the aggregator; clean encrypts with a local `sops` by the clone's `.sops.yaml`. That way the filter works without the aggregator, for example to recover with a backup key.

9. **Keeping plain text out of git** has three lines of defence:
   - the pre-commit hook from `forest git setup` (`forest secrets check --staged`);
   - `forest secrets check` in CI;
   - the deployment: `SopsDecryptStep` refuses if a file under `filter=forest` (by the `.gitattributes` of the stack directory and the directories above it) isn't encrypted. Deploying such a file would hide a leak; the secret is already in the history and must be changed.

10. **CLI commands:**
    - `forest secrets init`: `.sops.yaml` with the environments' keys (`--env`, `--rule <prefix>=<environment>`, `--recovery age1…`) and `.gitattributes`;
    - `adopt`: existing sops files under the filter, without changing `.sops.yaml`;
    - `add`: new files under the filter;
    - `status`: what's decrypted, for which environments, warnings about files of several environments;
    - `check`: the check for the hook and CI;
    - `updatekeys`: re-encrypting by the current `.sops.yaml`;
    - `remove`: a file out from under the filter: by default it stays a sops file in git and becomes ciphertext in the clone again (manual `sops` from then on); with `--plain --yes` the decrypted text goes into the next commit, for a file that turned out not to be a secret. Encrypted versions stay in the history. A file covered by a pattern gets a `-filter -diff` line;
    - `rule list | add <prefix>=<environment> | remove <prefix>`: `.sops.yaml` rules without rewriting the file. A new rule goes before the first one it overlaps (sops takes the first match) and gets the keys present in every rule that belong to no environment (the `init --recovery` backup keys). The CLI edits `.sops.yaml` as lines, without a YAML library (NativeAOT): unchanged rules stay as they were, and the commands refuse to change a shape they don't understand;
    - `forest git setup`: turn the filter on in a clone;
    - `forest git teardown`: turn it off: the filter config, its hook and the clone's cache are removed, and decrypted files become ciphertext again; uncommitted changes in them mean a refusal. `.gitattributes` and `.sops.yaml` stay: they belong to the repository.

## Consequences

- **Plain text in working copies.** Secrets sit in plain text in Editors' working copies: a deliberate trade-off for convenience, like any `sops decrypt` in a terminal. The working copy must be on a trusted machine. A token allowed to decrypt is for a workstation, not for CI.
- **Role changes.** Removing a role closes access to new decryptions, but not to what's already in clones: after people change, change the environment's secrets.
- **Dependence on the aggregator.** Without `--recovery` or your own key, only the aggregator decrypts the files: losing its data volume (the key ring) means losing the secrets. `forest secrets init` warns about this.
- **Separate rules per environment.** A file encrypted for several environments can be read by an Editor of any of them. `forest secrets init --rule` gives each environment its own rule, and `forest secrets status` warns about such files.
- **A stack's Editor.** A stack's Editor can decrypt the whole environment's secrets, not just their stack's: they're effectively root on the host anyway (see "Permissions" in `architecture.md`).
- **Audit.** The aggregator logs who decrypted which path, without content. The endpoints are rate-limited per user, so a stolen token can't become a fast decryption service. *Update (October 2026):* decryption is also recorded in the audit log, together with denied attempts.
- **Line endings.** *Update (October 2026):* git converts line endings before smudge, so on Windows with `core.autocrlf` ciphertext reached sops with CRLF and didn't decrypt (`parsing time "…Z\x0d"`), while `forest git setup` hid the reason behind a hint about permissions. Now `.gitattributes` rules are written with `eol=lf`, `forest secrets fix-eol` adds it to old rules and removes CRLF from git and the clone, `setup` shows the reason from the filter, `secrets check` rejects ciphertext with CRLF, and the aggregator names CRLF in the error. The filter encrypts plain dotenv and INI without CR: otherwise a value on the host would get a `\r`. Normalising ciphertext in the aggregator was rejected: a file the aggregator decrypts but a person's `sops` doesn't is worse than an explicit error.
