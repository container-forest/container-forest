# System settings and aggregator backup

Decisions: [ADR-0013](../adr/0013-system-settings.md) and [ADR-0014](../adr/0014-aggregator-backup.md). Aggregator paths are relative to `src/Aggregator`.

## Settings

`Application/Settings`: `SettingsSection` (fields), `SettingsStore` (reading and writing), `StoredSetting` in `Domain/Settings` (one field in the database).

- **A field's precedence:** the variable `CF_<SECTION>__<FIELD>` → the database → the default. `ResolvedSetting` says where the value came from (`Source`) and whether it's locked (`IsLocked`).
- **Writing:** `PreviewAsync` shows the result without saving, so the section can be validated as a whole. `SaveAsync` returns the names of the fields that really changed. A value equal to the default has no row in the database.
- **An invalid variable** doesn't fail the start: the field gets a `Problem`, visible in the panel and in the status.
- **A new section:** a class deriving from `SettingsSection` with fields and validation, a typed view, contracts with `SettingTextResponse` and `SettingSecretResponse`, endpoints, a screen. The section's service checks permissions itself, calls `IdentityConfirmations.Require` and writes to `IAuditTrail`.

## Identity confirmation

`IdentityConfirmations` issues and checks a token: the user id and the expiry time encrypted with Data Protection, 5 minutes, no server state. `Api/Access/IdentityConfirmer` checks a password or a passkey; sessions only, rate-limited like sign-in.

- `POST /api/auth/confirm/password`, `POST /api/auth/confirm/passkey/options`, `POST /api/auth/confirm/passkey`.
- A failure is a 403, not a 401: the session is intact, and a 401 would send the panel to the sign-in screen.
- The audit log records `IdentityConfirmed` with the outcome.
- The same confirmation is needed for `POST /api/me/passkeys` if the account has a password or a passkey (`PasskeyManagement.NeedsConfirmationAsync`). The panel learns this from `GET /api/me/passkeys/needs-confirmation`.
- **Deliberately left open:** an administrator can reset a password or create an administrator and confirm the action with it. The `AccountSecurityChanged` event reports this; see [ADR-0013](../adr/0013-system-settings.md).

## Backup

`Application/Backups`, `Infrastructure/Backups`, `Domain/Backups/BackupRun.cs`, `Api/Contracts/BackupContracts.cs`.

| Field | Variable | Default | Note |
|---|---|---|---|
| `Enabled` | `CF_BACKUP__ENABLED` | `false` | The schedule only; *Back up now* works without it |
| `IntervalHours` | `CF_BACKUP__INTERVALHOURS` | `24` | 1–168 |
| `Recipient` | `CF_BACKUP__RECIPIENT` | — | A public age key |
| `Endpoint` | `CF_BACKUP__ENDPOINT` | — | `https://` without a path; `http://` only for loopback |
| `Bucket` | `CF_BACKUP__BUCKET` | — | |
| `Region` | `CF_BACKUP__REGION` | `auto` | |
| `Prefix` | `CF_BACKUP__PREFIX` | empty | The start of the object name, e.g. `forest/` |
| `AccessKeyId` | `CF_BACKUP__ACCESSKEYID` | — | |
| `SecretAccessKey` | `CF_BACKUP__SECRETACCESSKEY` | — | Write-only |

**A run** (`BackupRunner`): `SqliteBackupArchiver` makes the archive, `S3BackupStorage` uploads it, `BackupRun` records the result (the last 100 are kept). The object is `<prefix>forest-<UTC time>.tar.gz.age`. A crash mid-run leaves no row, and the schedule simply tries again; the next run cleans up leftovers in `.backup-tmp`.

**API:** `GET`/`PUT /api/settings/backup`, `GET /api/backup`, `POST /api/backup/run` (waits for the end; a failed backup is a 200 with `outcome: Failed`, incomplete settings or a backup already running a 409). An administrator with an unrestricted token can read the state and start a run (`forest backup status`, `forest backup run`); changing settings is panel-only (`/backup`).

## Setting up Cloudflare R2

1. Create a bucket in Standard storage: the free tier doesn't apply to Infrequent Access.
2. Add a bucket lock rule for 30 days and a lifecycle rule deleting after 90.
3. Create an `Object Read & Write` API token for this bucket only; don't give it an admin token. Keep a separate `Object Read` token outside the aggregator: you need it to restore.
4. Generate the key on a separate machine: `age-keygen -o forest-backup.key`. Keep the private part outside the system, in two places.
5. Fill in the fields on the *Backup* screen, press *Back up now*, check the object in the bucket, and only then turn on the schedule.
6. Add a system-wide notification rule for `BackupFailed`, `SettingsChanged` and `AccountSecurityChanged`, and turn on 2FA for the Cloudflare account: the lock can be lifted through its dashboard.

## Restoring

You need the private age key, the bucket's read token and the host's `.env`. The aggregator's image must be no older than the one that made the backup.

```sh
# Download the object (if needed: aws s3 cp --endpoint-url <endpoint> s3://<bucket>/<object> .)
age --decrypt --identity forest-backup.key forest-<time>.tar.gz.age | tar -xz -C restored/

docker compose stop aggregator
# In the /data volume: remove aggregator.db*, put aggregator.db and data-protection-keys/ from restored/.
# The files must belong to the aggregator image's user.
docker compose start aggregator
```

Migrations are applied at start and git clones are fetched again. **After restoring:**

- Reissue API tokens and check users and roles: the old database brings back what was revoked after the backup.
- Enroll again the agents created after the backup.
- Deploy the stacks that changed after the backup: the database remembers the old commit.

This sequence (the archive from the bucket, decryption, starting the aggregator, signing in, reading a stored secret, a new backup) was checked by hand on RustFS over HTTP. It hasn't been checked on Cloudflare R2 yet: only a test with a local server checks the shape of requests over HTTPS.

## Backing up the whole disk yourself

You can leave the feature off, but the snapshot must be atomic (ZFS, LVM, a provider snapshot), or `aggregator.db` without a consistent `-wal` loses recent data. Back up `aggregator.db*` and `data-protection-keys/` together.

## Pitfalls

- Over HTTPS the SDK signs as a stream by default, which R2 doesn't accept. That's why `S3BackupStorage` sets `UseChunkEncoding = false`; over HTTP there's no difference, so test over HTTPS.
- The key in the database is itself inside the backup: restoring needs a key from outside the aggregator.
- `aggregator.db.before-migration` sits in the volume unencrypted, like the database itself; the key ring isn't in it. The next migration replaces it, so until then it holds stale data.
- Only system-wide rules receive the system events (`BackupFailed`, `SettingsChanged`, `AccountSecurityChanged`); the screen warns when there's no such rule.
- A dead aggregator won't tell you that backups stopped: that's the job of an external heartbeat, which doesn't exist yet.
