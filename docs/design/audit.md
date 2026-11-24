# Audit log

Code: `Domain/Audit`, `Application/Audit`, `Api/Contracts/AuditContracts.cs` (relative to `src/Aggregator`), `src/features/audit/model.ts` (relative to `src/Web`).

## The entry

An `AuditEntry` has four parts:

- **Who:** `AuditActor`: the id and the name at the time, the channel `Panel`/`ApiToken`/`Bot`/`Anonymous`, the token's id, the IP (none for the bot).
- **What:** an `AuditAction`; the `AuditCategory` section follows from the action.
- **On what:** the `AuditTarget` hierarchy: a user, a role, a token, an environment, a stack, a container by name, a file by path. The target supplies the environment and the stack for filtering.
- **Outcome:** `Succeeded`, `Denied`, `Failed`.

Names are copied and there are no foreign keys: the log survives renames and deletions.

An entry holds no secrets (invariant 6): no passwords, tokens or file contents, only names and paths. The failure reason is the message of a known exception (`AuditOutcomes.Of`); unknown ones become "Internal error.".

## How services write

Through `IAuditTrail`:

- **`RunAsync(new AuditDraft(action, target), work)`** runs the action and records the outcome. By exception: `AccessDeniedException` is `Denied`, anything else `Failed`, and `UnauthenticatedException` isn't recorded. Or through `draft.Fail(...)` when the action returned but didn't succeed, like compose with an error. `work` fills in `draft.Target` and `Details` once it has loaded the object.
- **`RecordAsync`** for a known outcome. For requests without a session (sign-in, the Discord callback, the bot's `/link`) `AuditPerson` sets the actor.

**A new action** that must show in the log: a new `AuditAction` value (stored by name, so order doesn't matter), a case in `AuditActions.CategoryOf`, a translation in the panel (`audit.actions`).

**Saving:** `AuditLog.AppendAsync` writes in its own `DbContext` from a new scope, without a cancellation token. A denied or failed attempt is recorded and the request's half-done changes aren't; a closed tab doesn't lose the entry. The price: the entry isn't in the same transaction as the action, so an action saved before the log write fails stays without an entry (the error goes to the aggregator's log and doesn't break the action).

## What is recorded

- **Sign-in and your own account:** password, passkey and Discord sign-in (a failed one with the name given and the reason), sign-out, changing your own password, linking and unlinking Discord.
- **Users:** every `UserManagementService` action (the action is `UserAccountAction.AuditAction`), creating and revoking API tokens.
- **Environments:** creation, changes and deletion, enrollment tokens, revoking the agent.
- **Stacks:** creation, deletion and source changes, the deploy hook, restart and down, manual deployments (panel, bot, CLI), rollbacks (`DeploymentQueued` with "Rollback to …") and releasing a hold (`StackHoldReleased`).
- **Containers:** start/stop/restart (the `AuditedCommand` decorator around `AuthorizedCommand`, so refusals too).
- **Secrets:** SOPS decryption for the git filter.
- **System:** identity confirmation, settings changes (field names only, no values), manual backups.

Webhooks, the CI deploy hook and rehydrate are in the deployment history, and scheduled backups in the backup history, not in the log.

## Reading and retention

- **API:** `AuditService` (an administrator without token restrictions), `AuditFilter` filters, pages of 1–200 entries, newest first.
- **Retention:** `AuditRetention` deletes entries older than `Audit:RetentionDays` (365 by default, 0 keeps them forever) at start and once a day.
- **Panel:** `/audit` (*Administration* → *Audit log*). Filters are in search params (`category`, `outcome`, `user`, `environment`, `q`, `from`/`to` as local days, `to` inclusive; `page`); search reaches the aggregator only on Enter; pages use `keepPreviousData`.
