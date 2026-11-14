# Image updates by the aggregator

Rules and reasoning are in [ADR-0015](../adr/0015-image-updates.md). This note covers where it lives in the code and details the ADR doesn't. Paths are relative to `src/Aggregator`.

## Model

- **Code:** `Domain/Images/` (`ImageName`, `ImagePolicy`, `ImageUpdateRecord`, `ImageTagCacheEntry`, `AggregatorSigningKey`), `Application/Images/`, `Infrastructure/Images/`; contracts in `Api/Contracts/ImageUpdateContracts.cs`.
- **`ImageName`** normalises a name the way Docker resolves it: `nginx` → `docker.io/library/nginx`, the host through `RegistryHost`. A policy and the compose service match only by `ImageName` equality, not by substring. A name with variables (`$`) isn't an `ImageName`.
- **`ImagePolicy`** is a separate entity with a `StackId` (cascading with the stack), up to `ImagePolicy.MaxPerStack` per stack, one per image (a unique index). The tag pattern is compiled with a timeout and must start with `^` and end with `\z`; `ImageTagOrder.TagTimestamp` needs a `ts` group. Timestamps are compared by length first, then as text (`ImagePolicy.CompareTimestamps`), so both numbers without leading zeros and the `20261109T120000` format work.
  - The last check's state is `LastCheckedAt`, `LastError`, `LatestTag`, `Warning`. `RecordFailed` says whether to notify: the first error after a successful check, and for an unreachable registry only from the third in a row (`RegistryFailures`).
  - `LastChangedByAdministrator`: whether the last change was made by a system or environment administrator (point 7 of the ADR).
  - The ADR's `Mode` field doesn't exist yet: there's only the commit mode, and the field arrives with the PR mode.
- **`ImageUpdateRecord`** records an update commit: `Commit`, `Parent`, `ComposePath`, the changes (`ImageChange`: service, from, to; a JSON column), the reason (`ImageUpdateReason`), who asked. Its id is known before the commit because the `Forest-Image-Update` trailer names it. Unique by (stack, commit), cascading with the stack; there's no foreign key to the policy: it may be deleted while its commits are still deployed.
- **`ImageTagCacheEntry`** caches tags (`ImageTagCache`, keyed by image and tag): the digest and `created`, shared by all policies for an image, surviving restarts.
- **Image hook:** the stack fields `ImageHookTokenHash` and `ImageHookAllowsDowngrade`, like the deploy hook's (`SecretToken`).
- **Write token:** `GitConnection.ProtectedPushToken` and `PushUserName` (`SecretPurpose.GitPushToken`). Moving the prefix to another host with any token set is a 400, as for the access token.

## Check and commit

`ImageUpdateService.CheckAsync`, under the stack's lock (`ImageUpdateLocks`; changing or deleting a policy takes the same lock):

1. Polling and the hook leave a pinned policy alone (`Pinned`).
2. Fetch the branch head (`GitAccessResolver.ForFetch`), take the stack's compose file from it, find the image's slots with `IComposeImageRewriter.Find`.
3. The target:
   - without a tag, `ImageTagSelector`: candidates matching the pattern; for `TagTimestamp`, the largest timestamp, reading only its manifest (the digest); for `ImageCreated`, `created` from the cache, with unknown ones read up to `ImageUpdateOptions.MaxTagReadsPerCheck` from the end of the tag list. Until everything has been read, `Waiting` (`CatalogIncomplete`). Different images with the same newest `created` are a policy error, not a random pick. More than `ImageTagSelector.ManyCandidates` candidates, or a 429, is a warning on the policy;
   - with a tag: the pattern, presence in the registry, direction by the policy's order. An older one from the hook without permission is a 409. A request by a person or a rollback pins the image.
4. Nothing to change: `UpToDate`; a dry run is `WouldUpdate` without writing (the tag cache is updated anyway).
5. An offline agent: `AgentOfflineException` (invariant 7). The write token is `GitAccessResolver.ForPush`, never the read token.
6. If the stack requires signatures, the head must be endorsed (`CommitEndorsementService` or approval by SHA) and descend from `DeployedCommit`.
7. `IComposeImageRewriter.Rewrite`, the commit with `IGitWriter.CreateCommitAsync` signed by `ICommitSigner`, the update record saved to the database **before** the push (the webhook from our own push may arrive before the push returns), `IGitWriter.PushAsync`. On `GitPushRejectedException`, fetch, check and change again, up to `ImageUpdateService.MaxPushAttempts` times.
8. The deployment works as for the deploy hook, under `PushDeploymentGate`: a queued unpinned deployment, or a running one with this commit, picks it up itself; another unfinished one gets a mark for `PushFollowUpObserver`; otherwise `Deployment.QueueFromImageUpdate`: the pinned commit, `pull: missing`, trigger `ImageUpdate`.

Registry, git and compose file errors aren't rethrown: the policy records them (`RecordFailed`), the notification is `ImageUpdateFailed`, and the result is `Failed` with a reason.

- **Rewriter:** `YamlComposeImageRewriter`: YamlDotNet scalar positions (`Mark.Index`, in characters), replacing from the end, keeping the quoting style; after the replacement the file is read again and every image compared (`Verify`). It refuses values with an anchor or an alias (a value that comes before its `image` key is an alias: the anchor is always earlier), with `<<`, block or YAML-tagged values, and values with variables. `Apply` replays recorded changes to verify a commit.
- **Git:** `LibGit2SharpGitSource` also implements `IGitWriter`: a tree with one new blob (keeping the file mode), `Commit.CreateBuffer`, signing outside the clone's lock, `ObjectDatabase.CreateCommitWithSignature`; the push goes through a temporary ref `refs/forest/image-update` without `+`, after which the clone's tracking branch moves to the new commit. `NonFastForwardException` and "fetch first" / "non-fast-forward" responses become `GitPushRejectedException`, anything else `DeploymentFailedException`.
- **The aggregator's key:** `AggregatorSigningKeyService`: created on the first update (`SshKeygenKeyGenerator`, `ssh-keygen -t ed25519`), with the private part under `SecretPurpose.AggregatorSigningKey`. Signing is `SshKeygenCommitSigner` (`ssh-keygen -Y sign -n git`, the key in a temporary 0700 directory and a 0600 file). Rotation keeps old keys with `RetiredAt`. `SigningKeyService` rejects the fingerprint of any of them.
- **Registry:** `OciRegistryClient` on its own `HttpClient` (no HTTP client factory: that would need a new package). Anonymous first, then on a 401 a Bearer token from the realm (the environment's login goes there only for the registry's own host, or `auth.docker.io` for Docker Hub), or Basic. Redirects are followed by hand, with `Authorization` only for the original host. `ConnectCallback` checks addresses while connecting and connects exactly to the checked ones (`GitHostGuard.IsPublic`), so DNS rebinding can't get around the ban on private addresses; `ImageUpdateOptions.AllowPrivateRegistries` lifts it. For an index, `created` comes from the first `linux` platform, not `unknown`.

## Endorsement

`CommitEndorsementService` was split out of `VerifyCommitStep`: the ADR-0011 signature check plus the update chain.

- If the signature gave no endorsement, with reason `Unsigned` or `UnknownKey` (the aggregator's key is unknown on purpose), a record is looked up by (stack, commit). Then, for each record in the chain, up to `CommitEndorsementService.MaxUpdateChain`: `ICommitInspector.DiffAsync`: the only parent equals `Parent`, only `ComposePath` changed and its mode didn't, and `IComposeImageRewriter.Apply` on the parent's file gives exactly the new one. A signed parent means endorsed, with `SignedBy` the root's signer; one approved by SHA (`IDeploymentRepository.WasApprovedAsync`) means endorsed without a signer; one that has a record of its own means one more step; anything else is `ImageUpdateMismatch`.
- `CommitEndorsement.ImageUpdate`, `Deployment.ImageUpdateId`; in responses, `EndorsementResponse`.
- **Exemption from the compose policy** (`ComposePolicyStep`): for `ImageUpdate`, the root's signer must administer the environment, and every policy in the chain must be `LastChangedByAdministrator`. A deleted policy gives no exemption.

## Running

- **Polling:** `ImageUpdatePoller` (every minute from `ImageUpdateScheduler`): the `ImageUpdateSettingsSection` settings (on/off, an interval of 1–1440 minutes, 5 by default), policies with `Poll` that aren't pinned, stacks with an online agent, each check in its own scope.
- **Hook:** `ImageHookService`, `POST /api/image-hooks/{stackId}`: the same 401 (`WebhookRejectedException`), policies with `AcceptRequests` (all, or by `image`), 404 (`ImageHookNotFoundException`), a tag without an image is a 400.
- **People:** `ImagePolicyService`: a stack's Editor adds, changes, removes, checks, pins and unpins; anyone who sees the stack can do a dry run. On save, the policy is checked against the branch head and requires a write token on the connection.
- **Audit:** `ImagePolicyAdded/Changed/Removed`, `ImagePinned/Unpinned`, `ImageHookCreated/Removed`, `GitPushCredentialChanged`, `AggregatorSigningKeyRotated`; a check by a person is `DeploymentQueued` with details.
- **CLI:** `forest stack image-policy list|add|change|remove|check|pin|unpin`, `forest stack image-hook create|downgrade|remove`, `forest connection set-push-token|remove-push-token`, `forest system signing-key show|rotate`. Polling settings are panel-only (the CLI has no `/api/settings/**`).

## Tests

- Domain: `ImagePolicyTests`; tag selection: `ImageTagSelectorTests`; the endorsement chain: `VerifyCommitStepTests`; the exemption: `ComposePolicyStepTests`.
- Infrastructure: `YamlComposeImageRewriterTests`, `OciRegistryClientTests` (fake HTTP), `GitWriterTests` with a local bare repository and a real `ssh-keygen` (skipped without it, failing in CI).
- API: `ImageUpdateTests`, with the registry, git writes and signing faked; the commit, push and deployment with the `ImageUpdate` trigger go through the real pipeline.
