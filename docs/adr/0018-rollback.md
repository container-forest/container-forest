# ADR-0018. Rolling back a stack: a pinned deployment of an earlier commit, and a hold

- Status: accepted
- Date: 2026-11-24

## Context

A bad deployment had no way back other than `git revert` and a push, or a commit by hand. That's slow at the moment it matters, and impossible when the git server is down or the branch is protected. The agent keeps the previous bundle in `<stack>.prev` (ADR-0004), but only the files of the one deployment before, without secrets, and without anything the aggregator checked about them (signatures, the compose policy).

Since image updates (ADR-0015) every stack commit names an exact image, so deploying an earlier commit brings back its images too.

Options considered:

1. **A rollback command on the agent** that swaps `.prev` back in. Only one step back; secrets aren't in `.prev`; the aggregator's checks (signatures, compose policy) are skipped, and the deployment history no longer describes what runs. Rejected.
2. **A `git revert` committed by the aggregator.** Git stays the whole truth, but it needs write access to every deployment repository (ADR-0015 has it only for repositories with image policies) and a working git server, and the revert commit has to be endorsed like any other. Rejected for now; a person can still revert in git.
3. **Deploying an earlier commit through the normal pipeline** (chosen). The aggregator already deploys a pinned commit for rehydration and image updates; a rollback is one more pinned trigger, with every check of a normal deployment.

A rollback alone doesn't last: the next push, deploy hook call or image update deploys the branch head again, which is what was rolled back.

## Decision

1. **A rollback is a deployment with the trigger `Rollback`**, pinned to a commit (`Deployment.QueueRollback`), started by a user with the role of a redeploy (Editor). It goes through every pipeline step. Images are pulled when missing.
2. **Only a commit the stack deployed successfully** can be rolled back to (`HasSucceededAsync`), so a rollback never deploys content nobody deployed to that stack, and needs no new rules about which commits a user may name. The API takes the full SHA (`POST /api/stacks/{id}/rollback`); the CLI also accepts a prefix found in the history.
3. **Endorsement (ADR-0011) is checked again**: a key may have been removed or its owner's role taken away since. The commit doesn't descend from the deployed one, which is expected and only noted in the log, since a person started it. For a stack requiring signed commits the user can approve the commit unsigned with the rollback (`approve`), as for a manual deployment.
4. **The hold.** A successful rollback sets `Stack.HeldCommit` and `HeldSince`. While a stack is held:
   - a push doesn't deploy it (`PushStackOutcome.Held`), nor does a push that arrived during the rollback;
   - the deploy hook answers 409, so the CI job shows that nothing was deployed;
   - image updates don't commit or deploy (`ImageUpdateOutcome.Held`, also for *Check now*); pinning a tag is a 409, a dry run still answers;
   - rehydration after a reboot deploys the held commit, as it deploys whatever runs.
5. **The hold ends** when someone deploys the branch by hand (a manual deployment that succeeds), or releases it (`DELETE /api/stacks/{id}/hold`, Editor, in the audit log as `StackHoldReleased`). Releasing deploys nothing; the next push, hook call or image update does.

## Consequences

- A rollback works with the git server down if the commit is in the aggregator's cache, as for rehydration.
- The stack's source may have changed since the commit was deployed. A commit missing from the new repository fails at the fetch step; a changed compose path reads the old commit at the new path. The history doesn't record the source of each deployment.
- Git no longer describes what runs while a stack is held; the panel, `forest stack show` and the history say so. To make it permanent, revert in git and deploy the branch.
- No `.proto` change: the agent receives an ordinary `DeployStack`. The bot can't roll back yet.
