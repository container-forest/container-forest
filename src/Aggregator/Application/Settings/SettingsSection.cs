using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Settings;

/// <summary>Where the value of a field comes from, highest priority first in the order of ADR-0013: environment, database, default.</summary>
public enum SettingSource
{
    Default,
    Database,
    Environment,
}

/// <summary>One field of a section. A secret is stored protected and never leaves the aggregator through the API.</summary>
/// <param name="Name">Also the last part of its environment variable: Backup + Bucket is CF_BACKUP__BUCKET.</param>
/// <param name="Normalize">Returns the value to keep, or throws <see cref="DomainValidationException"/>.</param>
public sealed record SettingField(string Name, bool IsSecret = false, string? Default = null, Func<string, string>? Normalize = null)
{
    public string Apply(string value) => Normalize is null ? value.Trim() : Normalize(value.Trim());
}

/// <summary>
/// A group of settings that belong together. Sections are types, not free keys: each one lists its fields with their
/// validation, and its own class gives the rest of the code typed values.
/// </summary>
public abstract class SettingsSection
{
    /// <summary>The name in the database, in the audit log and in environment variables.</summary>
    public abstract string Name { get; }

    public abstract IReadOnlyList<SettingField> Fields { get; }

    public SettingField Field(string name) =>
        Fields.Single(field => field.Name == name);
}

/// <summary>The current value of a field. A secret's value is only for the code that uses it.</summary>
public sealed record ResolvedSetting(SettingField Field, string? Value, SettingSource Source, string? Problem = null)
{
    public bool IsLocked => Source == SettingSource.Environment;

    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>The values of a section's fields, resolved as ADR-0013 says.</summary>
public sealed class ResolvedSettings(IReadOnlyDictionary<string, ResolvedSetting> fields)
{
    public ResolvedSetting this[string field] => fields[field];

    public IEnumerable<ResolvedSetting> All => fields.Values;
}
