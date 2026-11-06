using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Settings;

namespace ContainerForest.Aggregator.Application.Settings;

/// <summary>Values set outside the panel, by the environment of the aggregator's container.</summary>
public interface ISettingOverrides
{
    /// <summary>The value of the field; null when it is unset or empty.</summary>
    string? Find(string section, string field);
}

public interface IStoredSettingRepository
{
    Task<IReadOnlyList<StoredSetting>> ListAsync(string section, CancellationToken cancellationToken);

    void Add(StoredSetting setting);

    void Remove(StoredSetting setting);
}

/// <summary>
/// Reads and writes settings sections (ADR-0013). For each field the environment wins over the database, the database
/// over the default; a field the environment sets cannot be changed here. Callers check who may do this.
/// </summary>
public sealed class SettingsStore(
    ISettingOverrides overrides,
    IStoredSettingRepository settings,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<ResolvedSettings> ResolveAsync(SettingsSection section, CancellationToken cancellationToken)
    {
        var stored = (await settings.ListAsync(section.Name, cancellationToken)).ToDictionary(setting => setting.Field);
        var resolved = new Dictionary<string, ResolvedSetting>();
        foreach (var field in section.Fields)
        {
            resolved[field.Name] = Resolve(section, field, stored.GetValueOrDefault(field.Name));
        }

        return new ResolvedSettings(resolved);
    }

    /// <summary>
    /// What the section would be after the changes, with nothing saved, so a caller can check the whole before writing.
    /// </summary>
    /// <param name="changes">
    /// New values by field. Null or empty clears the field back to its default. Fields the environment sets are ignored.
    /// </param>
    /// <exception cref="DomainValidationException">A value is not valid for its field.</exception>
    public async Task<ResolvedSettings> PreviewAsync(
        SettingsSection section, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
    {
        var current = await ResolveAsync(section, cancellationToken);
        var result = new Dictionary<string, ResolvedSetting>();
        foreach (var field in section.Fields)
        {
            var setting = current[field.Name];
            if (setting.IsLocked || !changes.TryGetValue(field.Name, out var value))
            {
                result[field.Name] = setting;
                continue;
            }

            result[field.Name] = string.IsNullOrWhiteSpace(value)
                ? new ResolvedSetting(field, field.Default, SettingSource.Default)
                : new ResolvedSetting(field, field.Apply(value), SettingSource.Database);
        }

        return new ResolvedSettings(result);
    }

    /// <summary>Saves the changes; see <see cref="PreviewAsync"/>.</summary>
    /// <returns>The names of the fields that really changed, without any value.</returns>
    public async Task<IReadOnlyList<string>> SaveAsync(
        SettingsSection section, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
    {
        var current = await ResolveAsync(section, cancellationToken);
        var next = await PreviewAsync(section, changes, cancellationToken);
        var stored = (await settings.ListAsync(section.Name, cancellationToken)).ToDictionary(setting => setting.Field);
        var now = time.GetUtcNow();
        var changed = new List<string>();

        foreach (var field in section.Fields)
        {
            var before = current[field.Name];
            var after = next[field.Name];
            if (after.IsLocked || before.Value == after.Value)
            {
                continue;
            }

            changed.Add(field.Name);
            stored.TryGetValue(field.Name, out var row);

            // A value equal to the default keeps no row: a later change of the default then reaches this field too.
            if (after.Value is null || after.Value == field.Default)
            {
                if (row is not null)
                {
                    settings.Remove(row);
                }

                continue;
            }

            var value = field.IsSecret ? protector.Protect(SecretPurpose.StoredSetting, after.Value) : after.Value;
            if (row is null)
            {
                settings.Add(StoredSetting.Create(section.Name, field.Name, value, now));
            }
            else
            {
                row.Change(value, now);
            }
        }

        if (changed.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private ResolvedSetting Resolve(SettingsSection section, SettingField field, StoredSetting? stored)
    {
        if (overrides.Find(section.Name, field.Name) is { } fromEnvironment)
        {
            try
            {
                return new ResolvedSetting(field, field.Apply(fromEnvironment), SettingSource.Environment);
            }
            catch (DomainValidationException invalid)
            {
                // Not fatal at start: the feature is optional. The problem shows where the setting is used and in the panel.
                return new ResolvedSetting(field, null, SettingSource.Environment, invalid.Message);
            }
        }

        if (stored is null)
        {
            return new ResolvedSetting(field, field.Default, SettingSource.Default);
        }

        if (!field.IsSecret)
        {
            return new ResolvedSetting(field, stored.Value, SettingSource.Database);
        }

        try
        {
            return new ResolvedSetting(field, protector.Unprotect(SecretPurpose.StoredSetting, stored.Value), SettingSource.Database);
        }
        catch (SecretUnreadableException)
        {
            return new ResolvedSetting(field, null, SettingSource.Database, $"The stored {field.Name} cannot be decrypted; set it again.");
        }
    }
}
