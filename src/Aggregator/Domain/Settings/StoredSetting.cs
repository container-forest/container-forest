using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Settings;

/// <summary>
/// The value of one field of a settings section, set through the panel (ADR-0013). A field with no row has its value
/// from the environment or its default. The value of a secret field is stored protected.
/// </summary>
public sealed class StoredSetting : Entity<StoredSettingId>
{
    public const int MaxNameLength = 64;

    private StoredSetting(StoredSettingId id, string section, string field, string value, DateTimeOffset updatedAt)
        : base(id)
    {
        Section = section;
        Field = field;
        Value = value;
        UpdatedAt = updatedAt;
    }

    // For EF Core.
    private StoredSetting()
    {
        Section = null!;
        Field = null!;
        Value = null!;
    }

    public string Section { get; private init; }

    public string Field { get; private init; }

    public string Value { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static StoredSetting Create(string section, string field, string value, DateTimeOffset now) =>
        new(StoredSettingId.New(), section, field, value, now);

    public void Change(string value, DateTimeOffset now)
    {
        Value = value;
        UpdatedAt = now;
    }
}
