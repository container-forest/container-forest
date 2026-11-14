# ADR-0015. Image updates by the aggregator: a stack's image policy, a commit to the stack's repository, endorsement through an endorsed parent commit

- Status: accepted
- Date: 2026-11-08, changed 2026-11-09 (update tokens replaced by the stack's image hook)

## Context

A reproducible deployment needs an immutable image tag in compose (`docs/design/deployment.md`: `latest` only warns, because rolling back the stack's files doesn't bring back the old image). A future rollback from the panel will also rely on every stack commit naming an exact image. So someone has to commit each new build of an application to the stack's repository.

Today the application's CI does it: a script clones the deployment repository with a write token, changes the tag and pushes. With `RequireSignedCommits` (ADR-0011) such a commit is unsigned, and the deployment stops until it's approved by SHA. ADR-0011's consequences suggest a `ci` service user with a signing key, but:

- **The head's signature endorses the whole tree.** The CI key would also sign an unsigned commit someone slipped under its update with the CI token or through the git server. Checking the base in the CI script is pointless: the write token and the key sit in the same CI secrets.
- **CI secrets can be read by anyone who can push a workflow** to the application's repository, even on a side branch. The CI key means any change to the stack's compose for everyone with push access to the code, and a key shared by several applications means that for all their stacks.
- **Write access to the deployment repository** in CI also allows any change to compose, just without an automatic deployment.

A moving tag with a deploy hook (`:main` + `POST /api/deploy-hooks/{id}`) removes the key and the write access from CI, but git no longer describes what runs, and rolling back the files doesn't bring back the image.

Similar tools: Flux Image Automation (a controller reads the registry by a policy and commits the tag to git, and can sign the commit), Argo CD Image Updater (the same approach, write-back to git), Renovate / Dependabot (a PR with the new tag that a person merges).

Options considered:

1. **CI with a signing key** (ADR-0011's consequence). Nothing to build; the weaknesses above remain.
2. **A moving tag + the deploy hook.** No write secrets in CI, but it isn't GitOps: git doesn't describe the state, and there's no rollback.
3. **CI passes the tag, the aggregator commits.** The CI token picks the version but can't change anything except the image. Better than 1, but the token still decides what's deployed.
4. **The aggregator itself reads the registry by the stack's policy and commits** (chosen), and CI only speeds up the check. Explicitly requesting a specific tag stays possible within the policy.
5. **Overriding images in the aggregator's database without git** (like an override file). No git writes and no conflicts, but git stops being the whole truth, and a rollback has to follow deployments rather than commits. Rejected.
6. **A PR that a person merges with a signature.** The cleanest option for trusted environments; it will be a mode of the same mechanism (point 10), not a separate decision.

## Decision

### 1. A stack's image policy

An `ImagePolicy` is a record on a stack through which the stack's Editor lets the aggregator update one image:

| Field | Value |
|---|---|
| `Image` | the image repository without a tag or digest: `registry.example.com/team/app` |
| `TagPattern` | a regular expression for allowed tags, with `\z`: `^main-[0-9a-f]{7}\z` |
| `Order` | how to tell the newer tag: `ImageCreated` (the `created` field of the image's configuration, the default) or `TagTimestamp` (a named group `(?<ts>…)` in `TagPattern`). There's no lexical order: `main-<sha>` doesn't sort by it |
| `Services` | optional: update only these services; without it, every service with this image |
| `Poll` | the aggregator polls the registry itself (point 2); yes by default |
| `AcceptRequests` | accept requests through the stack's image hook: nudges and an explicit tag (points 2–3); yes by default |
| `PinDigest` | write `tag@sha256:…`; required if the stack has `RequireImageDigests` |
| `Mode` | `Commit` (point 5); `PullRequest` is deferred (point 10). While there's one mode, the model has no field: it arrives with the PR mode |
| `PinnedTag`, `PinnedBy` | a pin that stops automatic updates (point 3) |

- A stack can have several policies, one per image. The stack's Editor creates, changes and deletes them; all of it is audited (`ImagePolicyAdded/Changed/Removed`). A 409 for a system stack.
- **A match is by image, not by service.** One build often stands behind several services (a bot and its migrations), and they should move to a new version together. `Services` covers the opposite case (the same image in different versions). A request never names services, so the hook can't change a service the Editor didn't allow.
- **The comparison is exact**, by the normalised repository name (the default registry, `library/` for Docker Hub), not by substring.
- **The check at creation:** the policy must match at least one service of the stack's compose file at the branch head. An image with variables (`${TAG}`), a service with `extends` without its own `image`, and a service from an `include` file can't be rewritten by the aggregator, so such matches are rejected, as with `RequireImageDigests`.

### 2. When the aggregator checks the registry

Both automatic paths are turned on separately on each policy (`Poll`, `AcceptRequests`), because stacks need different things:

| `Poll` | `AcceptRequests` | When |
|---|---|---|
| yes | yes | the default: CI nudges at once, polling picks up a lost call |
| yes | no | CI holds no secret at all; updates lag by up to the interval |
| no | yes | updates only when CI asks; useful if more than this CI pushes to the registry |
| no | no | no automatic updates; the policy only limits which tags a person can pin from the panel or the CLI |

An explicit request by a person allowed to deploy doesn't depend on these flags. Changing the flags is audited (`ImagePolicyChanged`).

- **Polling:** a background service of the aggregator checks policies with `Poll` once per interval (a system setting, ADR-0013; 5 minutes by default, at least 1), only for stacks with an online agent and no pin. Limits: up to 10 policies per stack; with `TagTimestamp` the order is visible from the tags themselves, and only the newest one's manifest is read; with `ImageCreated` the aggregator reads manifests and configurations only for tags not yet in the cache, up to 100 per check (the rest in later checks). The cache is per tag: the policy is meant for immutable tags, so polling won't notice a re-pushed tag with the same name (a digest written into compose with `PinDigest` won't change because of it).
- **Choosing `Order`.** Both order by time; they differ in where the time comes from and how many requests it costs:

  | | `ImageCreated` (default) | `TagTimestamp` |
  |---|---|---|
  | Where the time comes from | the `created` field of the image's configuration | a timestamp in the tag string |
  | Requests per new tag | 2–3 (the manifest, for a multi-arch image also the platform's manifest, the configuration) | 0: the tag list is enough |
  | Changes in CI | none | a tag format such as `main-20261109T120000-7822307` |

  `ImageCreated` is the default because it works without changes in CI, and after the first check costs only a few requests per new build. That's enough for a registry of your own with tens or hundreds of tags. `TagTimestamp` is recommended when:
  - **there are thousands of tags:** the first check is thousands of requests, and with the limit of 100 per check it stretches over dozens of intervals; until it finishes, the aggregator doesn't update the image, since it doesn't yet know the newest tag;
  - **the registry has rate limits:** on Docker Hub a manifest `GET` counts as a pull, so reading manifests eats the limit shared with the agents' pulls;
  - **the build doesn't write a real date:** reproducible builds (`SOURCE_DATE_EPOCH`, ko, Nix, Jib) often set a constant date. If candidates have the same `created`, the aggregator doesn't update the image and shows a policy error rather than guessing.

  The documentation explains the choice, and the panel shows a warning on a policy with `ImageCreated` when the repository has more than 500 candidates or the registry answered 429. Nothing is blocked.

  In both cases the time is the build time (or the commit time, if CI takes the commit's date for the timestamp). Rerunning an old commit's workflow produces a "newer" image with old code; a timestamp from the commit's date avoids that.
- **A nudge:** `POST /api/image-hooks/{stackId}` with the stack's image hook secret (point 2a) in `Authorization: Bearer …` and a body `{"image"?: "…", "tag"?: "…"}`.
  - Without `image` the aggregator checks all the stack's policies with `AcceptRequests` now; with `image`, only that image's policy. The response lists the policies the request touched with each one's result.
  - Without `tag` the request carries no data: the aggregator looks at the registry itself, so a leaked secret only gives an earlier check. With `tag` it's an explicit request (point 3), and `image` is then required.
  - An image with no policy, or with a policy without `AcceptRequests`, is a 404 with the same response as a wrong secret: the endpoint doesn't reveal which images a stack has.
  - No stack, no hook and a wrong secret all get the same 401, as with the deploy hook.
- **The git server's package webhook** (Gitea has a `package` event) as a nudge without a secret in CI is **not part of this decision**; a separate ADR: every git server has its own format and signature. Polling already gives updates without any secret in CI.

### 2a. The stack's image hook

A stack secret modelled on the deploy hook (`docs/design/deployment-triggers.md`), not a user token:

- **The stack's Editor creates, rotates and deletes it;** the secret is shown once, and only its hash is stored (invariant 8). All of it is audited. A 409 for a system stack, as for the deploy hook.
- **Separate from the deploy hook.** The deploy hook redeploys the branch head with `pull: always`; the image hook changes a tag within the policy. They're different capabilities, and CI usually needs one of them.
- **`AllowDowngrade`** is a flag on the hook: allow an explicit request for an older tag (point 3). No by default.
- **Not a user action**, like a push or the deploy hook: the Editor who created the hook and the policy has already decided what's allowed; a call only says when. So in the update record and the audit log the reason is "image hook", not a person's name.
- **No expiry**, like the deploy hook: the secret is replaced by rotation. When a person leaves the team, rotate the secret as you would the deploy hook's.
- **What limits it:** the scope is one stack (in the URL), the images are the policies' `AcceptRequests`, the tags are `TagPattern` and presence in the registry, rollback is `AllowDowngrade`. There's no separate image filter: `AcceptRequests` on the policy plays that role, next to the image's other restrictions.
- **CLI:** `forest stack image-hook create <stack> [--allow-downgrade]` (a new secret), `downgrade <stack> on|off`, `remove`, with the state in `forest stack show`.

A user token with a scope (the system, environments or stacks) and an image filter was considered. Rejected: policies are set on each stack anyway, and a scope wider than a stack grows by itself to new stacks nobody decided to hand to CI. One build for several stacks means several hooks, or better, polling. A token with an owner would give automatic revocation along with the role and a name in the audit log; if that's ever needed, it can be added as a separate decision.

### 3. An explicit tag request and pinning

`{"image": "…", "tag": "main-7822307"}` asks for a specific tag. The aggregator checks that the image is in the stack's policy, the tag matches `TagPattern` and the tag exists in the registry (the digest comes from the registry, not the request).

| Who asks | Newer than deployed | Older (a rollback) |
|---|---|---|
| The image hook (default) | yes | 409 |
| The hook with the `AllowDowngrade` flag | yes | yes, with a pin |
| A person allowed to deploy the stack (panel, CLI) | yes | yes, with a pin |

- A person's request or a rollback **pins** the image (`PinnedTag`, `PinnedBy`): automatic updates of that image stop, or the next check would bring back the newest tag at once. The panel shows "pinned to main-dcef568 by …"; `forest stack image-policy unpin` removes the pin, and the next check takes the newest tag.
- An explicit request for a newer tag through the hook doesn't pin: it's only a faster automatic check. A pin by the hook (a rollback) shows as "pinned by the image hook".
- Pinning and unpinning are audited.

### 4. Changing the compose file

- The aggregator changes only the `services.<name>.image` scalars that match the policy, **by positions from the YAML parser**, not by re-serialising: comments, order and the rest of the bytes stay as they were. The stack's other files don't change.
- It writes `image:tag` or, with `PinDigest`, `image:tag@sha256:<digest>`. For multi-arch images, the index's digest.
- If the policy no longer finds anything at the branch head (compose changed), the update fails without writing and sends a notification: otherwise the pipeline would quietly stop.

### 5. Commit and push

1. The aggregator takes the stack's branch head (`GitFetchStep`) and checks it as an automatic ADR-0011 trigger: endorsed and descending from `DeployedCommit`. If the head isn't endorsed, no update is made, since the aggregator would otherwise endorse someone else's content under its own commit.
2. It creates a commit signed with the aggregator's key (point 6): its parent is that head, the author is `container-forest`, the message `deploy: <image name>:<tag> (<reason>)` with the reason `policy`, `requested by image hook` or `pinned by <user>`, and a trailer `Forest-Image-Update: <record id>`.
3. It pushes as a fast-forward. A rejection because of a new commit on the branch means fetch, re-check the new head, change again; up to three attempts, then an error and a notification.
4. It deploys **exactly the SHA it created** (a pinned deployment, as with `approveCommit`), not the head the git server would show. The new trigger in the history is `ImageUpdate`.
5. The webhook from the aggregator's own push gets `AlreadyRunning`/`AlreadyDeployed` through the existing `PushDeploymentGate` logic.

A stack's updates run one at a time.

**Writing to git.** ADR-0006 recommends a connection's service user without write access. Now a connection (or a repository) gets a **separate optional write account** (`PushCredential`), encrypted like other secrets (ADR-0003). A separate service user of the git server with write access only to deployment repositories, not to application code, is recommended. Without this account no image policy can be created. Branch protection on the git server must allow this user to push.

### 6. Endorsing update commits

The aggregator signs every update commit with its own key, but **endorsement doesn't depend on that key**: a key the aggregator trusted automatically would endorse anything after leaking. The update record provides the endorsement:

- **`ImageUpdateRecord`** in the aggregator's database: `Commit`, `Parent`, `StackId`, the changes (`service`, `from`, `to`, `digest`), the reason, who asked (the policy, the hook, a user), the time.
- **`VerifyCommitStep`**, when there's no signature or the key is unknown, looks up a record by (stack, SHA). The commit is endorsed if:
  1. the record exists and its `Parent` is the commit's actual single parent;
  2. the commit's tree differs from the parent's **only** in the recorded `image` lines of the stack's compose file (the aggregator recomputes the difference rather than trusting the record);
  3. the parent is endorsed: by a signature under ADR-0011, an approval by SHA, or by being an update commit itself (the chain is limited, e.g. to 100 steps; a longer one means "the update chain is too long" and needs a person's signature).
- The new endorsement status is `ImageUpdate`; the deployment shows the update record and whoever endorsed the chain's root (the root's `SignedBy`). The root owner's permissions are checked at deployment time, as for signed commits.

That way endorsement stays with a person, and the aggregator only adds an image change within a policy an Editor approved on top of endorsed content. Someone else's commit slipped under an update (point 5.1) or a commit swapped by the git server (point 6.2) don't pass.

**The aggregator's signing key.**

- **Why, if not for trust.** The repository's history stays uniform: on an endorsed stack every commit is signed, and an unsigned one stands out at once. The git server shows update commits as verified if the public key is added to the service user with `PushCredential`. People can check the author locally through `allowed_signers`.
- **One key per aggregator**, not per connection: it grants no trust, so there's nothing to separate. The same public key is added to the service users of different git servers. SSH `ssh-ed25519`. The aggregator generates it on first need and signs with `ssh-keygen -Y sign -n git` through `CliProcess`, as it verifies (ADR-0011). The private part is stored encrypted with Data Protection (ADR-0003) and goes into the backup (ADR-0014). The panel and `forest system signing-key show` show the public part.
- **This key never endorses content.** `VerifyCommitStep` doesn't look for it among `SigningKey`s, and `SigningKeyService` rejects an attempt to add its fingerprint to any user, so trust in it can't be turned on even by accident. A commit signed with the aggregator's key without an update record is an "unknown key", like any other.
- **Rotation** (`forest system signing-key rotate`, an administrator, audited) creates a new key. Old commits stay endorsed, since the record endorses, not the signature; it's worth keeping the old public key on the git server so the history still shows as verified.
- **A leaked key** only allows signing commits that the git server will show as verified. The aggregator won't deploy such commits: there's no update record for them. So the key is cosmetic for people and the git server, not a security boundary.

### 7. Exemption for endorsed content (ADR-0012)

An update commit inherits its parent's exemption from the compose policy **only if the image policy was last changed by an administrator or this environment's administrator**. Otherwise there's no exemption: an administrator allowed `privileged` for specific content, and an Editor mustn't substitute any image there through their image policy.

### 8. Permissions

| Action | Who |
|---|---|
| Image policy | the stack's Editor |
| The stack's image hook | the stack's Editor (point 2a) |
| An explicit request, pinning, unpinning | the stack's deploy permission (Editor) |
| `PushCredential` on a connection | whoever manages the connection (ADR-0006) |
| A nudge, an explicit request through the hook | the stack's image hook secret (point 2a) |

### 9. Registry access

The aggregator reads the registry through the OCI Distribution API (`/v2/<name>/tags/list`, `HEAD`/`GET` of a manifest, the configuration blob for `created`) with the environment's registry credentials it already stores (`ResolveRegistryAuthStep`). These are new outbound connections from the aggregator to registries. A manifest cache by digest, a limit on tags per request, timeouts from settings.

### 10. PR mode (deferred)

`Mode = PullRequest`: the aggregator pushes a branch `forest/image/<stack>` and opens a PR through the git server's API rather than committing to the stack's branch. The merge is a local `git merge -S` by a person allowed to deploy (ADR-0011), so for trusted environments endorsement stays entirely with a person. This needs API clients for Gitea/GitHub/GitLab and a separate ADR.

### 11. Notifications

- **A successful update** is a deployment with the `ImageUpdate` trigger, so the existing deployment notifications cover it. There's no new kind for it.
- **`ImageUpdateFailed`** (a stack event): the push was rejected after all attempts; the policy no longer finds anything in compose; the tag order is undetermined (equal `created`); the branch head isn't endorsed; the registry was unreachable for three checks in a row. The notification goes out on a policy's first error, and again only after a successful check, so polling doesn't send the same thing every five minutes.
- **`ImagePinned`** (a stack event): pinning and unpinning, with who did it, a person or the hook with `AllowDowngrade`. A pin turns off automatic updates, and the hook can pin an image without a person, so the team should know. The event also goes to the audit log.
- `bot.proto` gets new `NOTIFICATION_KIND_IMAGE_UPDATE_FAILED` and `NOTIFICATION_KIND_IMAGE_PINNED` (a compatible addition); an older bot shows them as "Notification". Existing rules don't receive the new events until they're added to a rule.

## Consequences

- **The application's CI no longer needs write access to the deployment repository or a signing key.** At most it holds the stack's image hook secret, which only speeds up the check or asks for a tag within the policy. The deploy hook stays for images with moving tags.
- **Every stack commit names an exact image**, so rolling back the files (`git revert`, a future rollback from the panel) brings back the image too.
- **What decides which version is deployed:** push access to the registry under the policy's image. Whoever can push an image with a tag matching `TagPattern` already decides which code runs in the container; that's not new.
- **A new attack surface: the aggregator's write access to git.** The aggregator already decrypts environments' secrets and has the highest trust, so the boundary doesn't move. Limit the write account to deployment repositories.
- **A compromised git server** can't slip in content: the aggregator deploys its own SHA, and a later deployment of a swapped commit fails point 6. It can only reject the push and delay the update.
- **ADR-0011 changes:** content can now be endorsed not only by people's and services' keys but also by the aggregator's update record on top of an endorsed commit. **ADR-0006 changes:** a connection gets an optional write account.
- The agent doesn't change. `bot.proto` only gets two new notification kinds (point 11).
- **What this doesn't cover:** the content of the image itself (cosign signatures are deferred, ADR-0011); images no policy covers; a compromised aggregator.
- The transition is gradual: a stack without an image policy works as before, and a CI with an update script can be replaced in one step.
