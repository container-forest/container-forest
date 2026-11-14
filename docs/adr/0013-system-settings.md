# ADR-0013. System settings: typed sections, environment precedence and identity confirmation

- Status: accepted
- Date: 2026-11-06

## Context

Until now all of the aggregator's configuration was set by `CF_*` variables and read once at start. That's not enough for optional features: backups (ADR-0014), later external sign-in providers. They're convenient to turn on from the panel, without access to the host, and they contain secrets. At the same time, some administrators want to set everything through the environment and not let the panel change it.

Such settings are sensitive: whoever changes the backup's recipient and bucket can get a copy of every secret; whoever adds a sign-in provider gets a permanent way in. A stolen session mustn't be able to do this alone.

Storage options:

1. **An `IConfigurationProvider` over the database.** Values would reach options automatically, but the panel couldn't tell where a value came from, and decrypting a secret needs Data Protection, which itself is configured from configuration.
2. **Free-form key-value pairs with an API.** Simple, but validation and permissions spread across clients, and secrets are easy to return in a response.
3. **Typed sections with storage in `Application`.** Each section lists its fields with validation, and the rest of the code gets a typed value.

## Decision

1. Option 3: `SettingsSection` describes fields, `SettingsStore` reads and writes them, and a `StoredSetting` database row is one field.
2. **Precedence per field:** environment variable → database → default. A field from a variable is locked in the panel, and a PUT ignores it. An invalid variable value doesn't fail the start: the feature is optional, so the problem is shown in the panel and in the run's result.
3. **What doesn't go into sections:** anything the aggregator can't start without, or that's needed before reading the database (the connection string, the key ring directory, the domain). Existing settings aren't moved over wholesale.
4. **Secrets** are stored through `ISecretProtector` (`SecretPurpose.StoredSetting`). The API returns only `isSet`. The audit log and notifications name fields, not values.
5. **Changing a sensitive section** needs an administrator, a session (not an API token) and a fresh identity confirmation: a password or a passkey gives a 5-minute token (`IdentityConfirmations`) bound to the user. Successful and failed attempts go to the audit log, and a failed password counts towards lockout. Signing in with Discord doesn't count as confirmation: a repeated OAuth often goes through without any action by the person.
6. **Notifications.** A change produces a `SettingsChanged` event. The event concerns the whole system, so `NotificationEvent.EnvironmentId` became optional; only system-wide rules receive such events.
7. Changes apply without a restart: a consumer reads the section whenever it needs it. There's one aggregator, so no synchronisation between instances is needed.

## Consequences

- A new section is a new class, contract and screen; the storage and the confirmation don't change. A section for OIDC will need dynamic authentication schemes, since ASP.NET registers them at start.
- The CLI doesn't change these settings: it has no session. Environment variables are there for automation.
- **Adding a passkey** also needs confirmation, otherwise a stolen session could add its own key and confirm with it. The exception is a Discord-only account: it has nothing to confirm with, so its first passkey is added without it.
- **The confirmation's limits.** An administrator's session or token can still reset a password (their own or another administrator's) or create a new administrator, and then confirm the action with the new password. That's user management, including from the CLI, and it's deliberately unchanged: mandatory confirmation would break those CLI commands, since a token can't confirm itself. So against a stolen administrator session the confirmation is only an obstacle, and the second line of defence is the audit log and notifications: a new `AccountSecurityChanged` event reports granting administrator rights, creating an administrator and resetting a password.
- Changing environment variables stays outside the audit log and notifications: only the host sees them.
- An administrator who signs in only with Discord adds a passkey first.
