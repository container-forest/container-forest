using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Backups;

namespace ContainerForest.Aggregator.Api.Contracts;

// Settings of the system and the aggregator's backup, for administrators (ADR-0013, ADR-0014).
//
//   POST /api/auth/confirm/password          -> IdentityConfirmationResponse; 403 for a wrong password
//   POST /api/auth/confirm/passkey/options   -> options for navigator.credentials.get()
//   POST /api/auth/confirm/passkey           -> IdentityConfirmationResponse; 403 for a failed check
//
//   GET  /api/settings/backup                -> BackupSettingsResponse
//   PUT  /api/settings/backup                -> BackupSettingsResponse; a session and a fresh confirmation
//   GET  /api/backup                         -> BackupStatusResponse
//   POST /api/backup/run                     -> BackupRunResponse, when it has finished; 409 when it cannot start
//
// - A field set in the aggregator's environment (CF_BACKUP__BUCKET) reads "locked" and keeps its value on PUT.
// - A secret is never returned, only whether it is set.
// - In PUT an empty text field goes back to its default; a null secretAccessKey keeps the stored one, an empty one removes it.
// - A backup that ran and failed is a 200 with outcome Failed: the request itself went fine.

/// <param name="Token">For the action that asked for it; valid only for this user until it expires.</param>
public sealed record IdentityConfirmationResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record ConfirmWithPasswordRequest(string Password);

/// <param name="Credential">The result of <c>navigator.credentials.get()</c>, serialized with <c>toJSON()</c>.</param>
public sealed record ConfirmWithPasskeyRequest(string Credential);

/// <param name="Locked">Set in the environment, so it cannot be changed here.</param>
/// <param name="Problem">Why the environment's value is not used.</param>
public sealed record SettingTextResponse(string? Value, SettingSource Source, bool Locked, string? Problem);

/// <param name="IsSet">Whether a value exists; the value itself never leaves the aggregator.</param>
public sealed record SettingSecretResponse(bool IsSet, SettingSource Source, bool Locked, string? Problem);

public sealed record BackupSettingsResponse(
    SettingTextResponse Enabled,
    SettingTextResponse IntervalHours,
    SettingTextResponse Recipient,
    SettingTextResponse Endpoint,
    SettingTextResponse Bucket,
    SettingTextResponse Region,
    SettingTextResponse Prefix,
    SettingTextResponse AccessKeyId,
    SettingSecretResponse SecretAccessKey);

/// <param name="SecretAccessKey">Null keeps the stored key; empty removes it.</param>
/// <param name="Confirmation">The token from /api/auth/confirm/*.</param>
public sealed record UpdateBackupSettingsRequest(
    bool Enabled,
    int IntervalHours,
    string? Recipient,
    string? Endpoint,
    string? Bucket,
    string? Region,
    string? Prefix,
    string? AccessKeyId,
    string? SecretAccessKey,
    string? Confirmation);

/// <param name="ObjectKey">Where the archive went, relative to the bucket; null for a failed run.</param>
public sealed record BackupRunResponse(
    Guid Id,
    BackupTrigger Trigger,
    BackupOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? ObjectKey,
    long? SizeBytes,
    string? Error);

/// <param name="Problems">What stops a backup; empty when it can run.</param>
/// <param name="NextRunAt">Null while backups are off.</param>
/// <param name="NotifiesFailures">A notification rule on the whole system covers failed backups.</param>
/// <param name="NotifiesChanges">A notification rule on the whole system covers changes of settings.</param>
/// <param name="Runs">The latest runs, newest first.</param>
public sealed record BackupStatusResponse(
    bool Enabled,
    IReadOnlyList<string> Problems,
    bool IsRunning,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastSucceededAt,
    bool NotifiesFailures,
    bool NotifiesChanges,
    IReadOnlyList<BackupRunResponse> Runs);
