# ADR-0014. Aggregator backup: an encrypted archive in an S3-compatible bucket, refining invariant 1

- Status: accepted
- Date: 2026-11-06

## Context

The `/data` volume holds the database, the Data Protection key ring and (encrypted with it) the environments' private age keys. The key ring isn't encrypted on disk (ADR-0003), so the database together with it yields every stored secret. Losing the volume means losing those keys, and with them access to the secrets in repositories unless there's a backup key. So far the documentation only said to back up the volume, and there was no feature for it.

The database runs in WAL mode, so a plain copy of the files on the fly may be inconsistent. Backing up the whole disk with atomic snapshots solves this, so the feature must be optional.

Options:

1. **An external cron job on the host.** Simple, but it breaks silently, so without notifications and invisible in the panel.
2. **The aggregator itself.** It can take a consistent snapshot, show the state and send a failure notification. A dead aggregator makes no new copies, but the old ones remain.
3. **Moving to PostgreSQL.** Doesn't solve the problem: the key ring is files anyway, and copying the files of a running cluster is unreliable too.

Where to: an S3-compatible bucket at a different provider than the aggregator's host (an account lockout mustn't take both the server and the copies). Worse options: through an agent to another host (a new command and file transfer over the stream) or into Gitea (which dies with the aggregator's host).

## Decision

1. The aggregator makes the backup. Off by default; the settings are the `Backup` section (ADR-0013).
2. **The archive** is a `tar.gz` with a database snapshot (`VACUUM INTO`, then `PRAGMA integrity_check`) and the key ring. The key ring is copied after the database, so it has every key for the snapshot's values. Git clones aren't included: they can be fetched again.
3. **Encryption** is `age` to a single recipient. The aggregator has only the public key; the private one is kept outside the system. Unencrypted files exist only during a run, in a `0700` directory on the same volume as the database.
4. **Upload** is `AWSSDK.S3` to any S3-compatible server (Cloudflare R2, MinIO). The object name contains the time, so objects aren't overwritten. The request is an ordinary one with a signed file hash: R2 doesn't accept the SDK's streaming signing. HTTPS is required; HTTP only for loopback.
5. **Schedule.** Once a minute `BackupScheduler` checks whether it's time, by the last run's time in the database. After a failure the next attempt is in an hour. A `BackupFailed` notification goes out on the first failure of a series and then once per UTC day. One run at a time (`BackupGate`).
6. **Protection against deletion** is on the bucket's side: a 30-day lock rule and lifecycle deletion after 90. The aggregator deletes nothing.
7. **Before a database migration** the aggregator leaves `aggregator.db.before-migration` next to it (a snapshot, replaced by the next migration) regardless of settings: a migration can't be undone. A failed copy doesn't stop the start.
8. **Invariant 1 is refined:** in readable form the private age keys exist only on the aggregator, and the backup takes them out encrypted to an offline key. Agents never receive them in any form.

## Consequences

- New dependencies: the `AWSSDK.S3` package and the `age` binary in the aggregator's image (`scripts/install-sops.sh`).
- Losing the offline key makes every backup useless. The bucket's read key must also be kept outside the aggregator: the key in the database is inside the backup itself.
- A compromised aggregator can change the recipient for future copies, so the change needs confirmation and sends a notification (ADR-0013). It can't read or erase copies already stored.
- Restoring from an old backup brings back revoked tokens and roles that shouldn't exist any more, and agents enrolled later have to enroll again. The restore procedure describes this.
- The aggregator can't check that a copy works: it has no private key. Try restoring by hand from time to time.
- The host's `.env` isn't in the archive: the aggregator doesn't read it.
