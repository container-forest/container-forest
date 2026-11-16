using System.Globalization;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>
/// How long resource usage is kept and when it becomes a notification (ADR-0017). Each field can also be set with
/// CF_METRICS__&lt;FIELD&gt;, which locks it in the panel.
/// </summary>
public sealed class MetricsSettingsSection : SettingsSection
{
    public const string SectionName = "Metrics";
    public const string MinuteRetentionDaysField = "MinuteRetentionDays";
    public const string HourRetentionDaysField = "HourRetentionDays";
    public const string AlertsEnabledField = "AlertsEnabled";
    public const string HostCpuPercentField = "HostCpuPercent";
    public const string HostMemoryPercentField = "HostMemoryPercent";
    public const string DiskPercentField = "DiskPercent";
    public const string ContainerMemoryPercentField = "ContainerMemoryPercent";
    public const string AlertMinutesField = "AlertMinutes";

    public static readonly MetricsSettingsSection Instance = new();

    public override string Name => SectionName;

    public override IReadOnlyList<SettingField> Fields { get; } =
    [
        new(MinuteRetentionDaysField, Default: "7", Normalize: v => Integer(v, 1, 90, "Minute points are kept for 1 to 90 days.")),
        new(HourRetentionDaysField, Default: "90", Normalize: v => Integer(v, 1, 1825, "Hour points are kept for 1 to 1825 days.")),
        new(AlertsEnabledField, Default: "true", Normalize: Flag),
        new(HostCpuPercentField, Default: "90", Normalize: Percent),
        new(HostMemoryPercentField, Default: "90", Normalize: Percent),
        new(DiskPercentField, Default: "90", Normalize: Percent),
        new(ContainerMemoryPercentField, Default: "90", Normalize: Percent),
        new(AlertMinutesField, Default: "5", Normalize: v => Integer(v, 1, 120, "An alert waits 1 to 120 minutes.")),
    ];

    private static string Percent(string value) => Integer(value, 0, 100, "A threshold is a percentage from 1 to 100, or 0 to turn it off.");

    private static string Integer(string value, int min, int max, string message) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
            ? number.ToString(CultureInfo.InvariantCulture)
            : throw new DomainValidationException(message);

    private static string Flag(string value) =>
        bool.TryParse(value, out var flag)
            ? flag ? "true" : "false"
            : throw new DomainValidationException("The value must be true or false.");
}

/// <summary>The typed view of <see cref="MetricsSettingsSection"/>. A threshold of 0 is off.</summary>
public sealed record MetricsSettings(
    int MinuteRetentionDays,
    int HourRetentionDays,
    bool AlertsEnabled,
    int HostCpuPercent,
    int HostMemoryPercent,
    int DiskPercent,
    int ContainerMemoryPercent,
    int AlertMinutes)
{
    public static readonly MetricsSettings Default = new(7, 90, true, 90, 90, 90, 90, 5);

    public TimeSpan AlertDelay => TimeSpan.FromMinutes(AlertMinutes);

    public static MetricsSettings From(ResolvedSettings settings)
    {
        int Number(string field, int fallback) =>
            int.TryParse(settings[field].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        return new MetricsSettings(
            Number(MetricsSettingsSection.MinuteRetentionDaysField, Default.MinuteRetentionDays),
            Number(MetricsSettingsSection.HourRetentionDaysField, Default.HourRetentionDays),
            settings[MetricsSettingsSection.AlertsEnabledField].Value != "false",
            Number(MetricsSettingsSection.HostCpuPercentField, Default.HostCpuPercent),
            Number(MetricsSettingsSection.HostMemoryPercentField, Default.HostMemoryPercent),
            Number(MetricsSettingsSection.DiskPercentField, Default.DiskPercent),
            Number(MetricsSettingsSection.ContainerMemoryPercentField, Default.ContainerMemoryPercent),
            Number(MetricsSettingsSection.AlertMinutesField, Default.AlertMinutes));
    }
}
