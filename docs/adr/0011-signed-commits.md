# ADR-0011. Endorsing stack content with commit signatures

- Status: accepted
- Date: 2026-11-03
- Changed: [ADR-0015](0015-image-updates.md): the aggregator's image update commit is endorsed by its record on top of endorsed content, not by a key.

## Context

The aggregator's permissions decide **who starts** a deployment, but not **what** is deployed: the content is the branch head on the git server. So effectively everyone who can change the branch is root on the stack's hosts:

- anyone with push access to the repository, even without an account in container-forest. With `DeployOnPush` a push is a deployment; without it, waiting for a manual one is enough;
- the git server's administrator, or a compromised git server: it rewrites the branch and, knowing the webhook secret, starts a deployment itself;
- anyone with a stolen clone with write access, or a CI token.

This also gets around ADR-0010: only an environment's Editor can decrypt its secrets, but someone who can push adds a service with `env_file: .env` to compose that sends the content out, and the aggregator decrypts the file itself during the deployment. **Push access almost equals access to the environment's plain-text secrets.**

In real deployments this isn't theoretical: the git server and the CI runner often run on a host trusted less than the aggregator. A compromised less trusted host then controls more trusted ones.

Options considered:

1. **Approving every deployment in the panel.** Closes the problem, but kills deploy on push and the CI hook, i.e. GitOps as such.
2. **Relying on the git server**: branch protection, *Verified* in Gitea or GitHub. The git server stays in the trusted base, and surviving its compromise is exactly what we want.
3. **GPG signatures.** A separate key infrastructure, complicated verification, and few people have a GPG key.
4. **SSH commit signatures verified by the aggregator with users' keys** (chosen). Git supports them since 2.34 (`gpg.format=ssh`), people already have keys, and `ssh-keygen -Y verify` does the verification.
5. **Image signatures (cosign).** An addition, not a replacement: it protects the image, but not the compose file and secrets. Deferred.

## Decision

1. **Signing keys** (`SigningKey`, tables `SigningKeys`, `SigningKeyEnvironments`) are users' public SSH keys: `ssh-ed25519`, `ecdsa-sha2-nistp256/384/521`, the hardware `sk-ssh-ed25519@openssh.com` and `sk-ecdsa-sha2-nistp256@openssh.com`, and `ssh-rsa` of at least 2048 bits. A key uniquely determines a SHA256 fingerprint: one key can't belong to two users. Up to 20 keys per user. Only the public part is stored (invariant 8 isn't affected).

2. **Limited trust in a key.** A key has a scope: all environments (the default, for people) or a list. The scope only narrows its owner's rights, like an API token's restrictions (ADR-0007): a key endorses a stack's deployment if `AccessPolicy` allows the deployment for the actor "the key's owner with the key's scope". Being restricted is a separate flag, so a key whose environments were all removed by the cascade endorses nothing rather than everything. Restrict a key on a less trusted machine (a CI runner, a work laptop) to environments of the same level of trust.

3. **Who manages keys.** Only a panel session adds, changes and deletes your own keys (`GetSignedInUserAsync`), not an API token: otherwise a read-only token could add a key and gain the right to deploy through a push. An administrator sees and deletes anyone's keys and can add a key for another user: a `ci` service user without a session. Such a key records who added it, and the panel shows that next to the signature, so an administrator can't quietly sign in someone else's name. Every key change is in the audit log.

4. **Stack options**, both off by default, turned on and off by the stack's Editor, audited:
   - `RequireSignedCommits`: deploy only endorsed content;
   - `RequireImageDigests`: every image with a digest (`@sha256:…`): a signature covers `image: app:1.4`, not what's behind the tag in the registry, and whoever can push images could otherwise get around the signature. An image with variables (`${TAG}`) and a service with `extends` without its own `image` are rejected in this mode: they can't be checked before substitution.

5. **What a signature endorses.** A commit refers to a tree, so the branch head's signature covers the repository's whole state, including unsigned commits under it: whoever signed endorses everything, as a reviewer does with their merge.

6. **Verification** is the `VerifyCommitStep` right after `GitFetchStep` and **before `SopsDecryptStep`**: unsigned content never gets plain-text secrets. A commit is addressed by hash, so what's verified and what's read are the same. The order:
   1. the signature from the commit (LibGit2Sharp's `Commit.ExtractSignature`); none means "unsigned", a non-SSH one "unsupported signature";
   2. the aggregator parses the SSHSIG, takes the public key from it and looks up a `SigningKey` by fingerprint; none means "unknown key";
   3. `ssh-keygen -Y verify -n git` with an `allowed_signers` holding only this key; a failure means "invalid signature". The cryptography is checked before permissions, so a forged signature can't claim someone else's name even in a message;
   4. the owner is active and has the deploy permission on the stack within the key's scope; a key outside its scope means "the key doesn't cover the environment", no permission means "the owner may not deploy the stack". Permissions are checked at deployment time, not signing time: the commit's date is under the author's control, and an SSH signature has no trusted time. A removed role or a deleted key takes effect at once, for old commits too.

7. **Triggers.**

   | Trigger | What's required |
   |---|---|
   | Webhook, deploy hook | an endorsed commit that descends from `DeployedCommit` (or is the same) |
   | Manual (panel, CLI, bot) | an endorsed commit; not descending is only a warning in the log |
   | Manual with approval | an unsigned commit deploys if it's exactly the SHA a person allowed to deploy approved (`approveCommit`); otherwise an error |
   | Rehydrate | the commit equals `DeployedCommit`: the content is already running, the signature isn't checked |

   - **Descending from `DeployedCommit`.** Without it, a force push to an old signed commit would roll the stack back to a version with an old vulnerability: the signature is valid, but the signer never meant a rollback. A deliberate rollback is a manual deployment.
   - **Approval is pinned to a SHA.** Approving "deploy the head" without a commit can be gamed: "Alice, please deploy" right after your own push. So the panel offers to approve the commit the deployment stopped at, and a deployment with approval fails if the branch has moved on meanwhile. The bot can't approve: Discord doesn't show what's in the commit.
   - **Rehydrate without verification**, because otherwise after a host reboot a stack would stay down if its signer had lost their role in the meantime. Rehydrate deploys no new content.

8. **The result in a deployment**: the endorsement status (not checked, signed, approved by hand, already deployed, rejected), who signed (id and name at the time), the key's fingerprint, the reason for a refusal. A webhook deployment, which used to have no user, now shows in the history who endorsed the content. A refusal reveals no secrets: it happens before decryption.

9. **The tool** is `ssh-keygen` from `openssh-client` in the aggregator's image (`Signatures:SshKeygenPath`, `Signatures:Timeout`), run without a shell and with a cleared environment, like `sops` (`CliProcess`). Verifying SSHSIG in .NET itself was rejected: the BCL has no Ed25519, and our own cryptography is code we'd have to test ourselves.

## Consequences

- **A merge in the web interface** of Gitea or GitHub is signed with the platform's key, which the aggregator doesn't know: such a commit isn't deployed automatically, only by approval in the panel. Adding the git server's key as trusted isn't allowed: that would put it back into the trusted base. The process for endorsed stacks is a local `git merge -S` (or a fast-forward to a commit signed by an author allowed to deploy), or approval in the panel.
- **CI on a less trusted host** (a common setup): a `ci` service user with the Editor role only on stacks of less trusted environments and a key restricted to them. For trusted environments, `RequireSignedCommits` and `RequireImageDigests`; an image update arrives as a PR with a new digest that a person merges with a signature. The deploy hook isn't needed there: with digests it changes nothing, and its token sits on the less trusted host.
- **Changing a stack's source** (another repository or branch) makes the new commit not a descendant of the deployed one: automatic deployments stop until the first manual one.
- **What signatures don't cover**: a compromised aggregator; a malicious stack Editor (they're root anyway; the compose policy provides that boundary); vulnerabilities in libgit2's packfile parsing, since the fetch happens before verification; images with moving tags unless `RequireImageDigests` is on.
- The webhook secret on the git server stops mattering for endorsed stacks: a compromised git server can only start deployments of already endorsed content.
- Nothing changes for the agent, the bot or `.proto`.
- Not part of this decision and left for later: security notifications (a new key, a failed verification), declaring stack placement in the repository (it could build on the same keys), image signatures, GPG and X.509.
