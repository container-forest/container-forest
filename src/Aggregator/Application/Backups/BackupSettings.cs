using System.Globalization;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Backups;

/// <summary>The settings of the aggregator's backup (ADR-0014): off by default, for those who back up the whole disk.</summary>
public sealed partial class BackupSettingsSection : SettingsSection
{
    public const string SectionName = "Backup";
    public const string EnabledField = "Enabled";
    public const string IntervalHoursField = "IntervalHours";
    public const string RecipientField = "Recipient";
    public const string EndpointField = "Endpoint";
    public const string BucketField = "Bucket";
    public const string RegionField = "Region";
    public const string PrefixField = "Prefix";
    public const string AccessKeyIdField = "AccessKeyId";
    public const string SecretAccessKeyField = "SecretAccessKey";

    public const int MinIntervalHours = 1;
    public const int MaxIntervalHours = 168;
    public const int DefaultIntervalHours = 24;
    public const string DefaultRegion = "auto";

    public static readonly BackupSettingsSection Instance = new();

    public override string Name => SectionName;

    public override IReadOnlyList<SettingField> Fields { get; } =
    [
        new(EnabledField, Default: "false", Normalize: NormalizeFlag),
        new(IntervalHoursField, Default: DefaultIntervalHours.ToString(CultureInfo.InvariantCulture), Normalize: NormalizeInterval),
        new(RecipientField, Normalize: AgeKey.NormalizeRecipient),
        new(EndpointField, Normalize: NormalizeEndpoint),
        new(BucketField, Normalize: NormalizeBucket),
        new(RegionField, Default: DefaultRegion, Normalize: NormalizeRegion),
        new(PrefixField, Normalize: NormalizePrefix),
        new(AccessKeyIdField, Normalize: NormalizeAccessKeyId),
        new(SecretAccessKeyField, IsSecret: true, Normalize: NormalizeSecret),
    ];

    private static string NormalizeFlag(string value) =>
        bool.TryParse(value, out var flag)
            ? flag ? "true" : "false"
            : throw new DomainValidationException("The value must be true or false.");

    private static string NormalizeInterval(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is >= MinIntervalHours and <= MaxIntervalHours
            ? hours.ToString(CultureInfo.InvariantCulture)
            : throw new DomainValidationException($"The interval must be from {MinIntervalHours} to {MaxIntervalHours} hours.");

    /// <summary>
    /// HTTPS only, because the request carries a signature made with the key; plain HTTP is for a server on this very host.
    /// </summary>
    private static string NormalizeEndpoint(string value)
    {
        var isUsable = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0;
        return isUsable
            ? uri!.GetLeftPart(UriPartial.Authority)
            : throw new DomainValidationException("The endpoint must be an https:// address without a path, e.g. https://<account>.r2.cloudflarestorage.com.");
    }

    private static string NormalizeBucket(string value) =>
        BucketPattern().IsMatch(value)
            ? value
            : throw new DomainValidationException("A bucket name is 3-63 lowercase letters, digits, dots and hyphens.");

    private static string NormalizeRegion(string value) =>
        RegionPattern().IsMatch(value) ? value : throw new DomainValidationException("A region is lowercase letters, digits and hyphens.");

    private static string NormalizePrefix(string value) =>
        PrefixPattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal)
            ? value
            : throw new DomainValidationException("A prefix is up to 100 letters, digits, '-', '_', '.' and '/', and does not start with '/'.");

    private static string NormalizeAccessKeyId(string value) =>
        AccessKeyIdPattern().IsMatch(value) ? value : throw new DomainValidationException("An access key id has no spaces and is up to 128 characters.");

    private static string NormalizeSecret(string value) =>
        value.Length <= 256 ? value : throw new DomainValidationException("A secret access key is up to 256 characters.");

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z")]
    private static partial Regex BucketPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RegionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-][A-Za-z0-9_./-]{0,99}\z")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"^[\x21-\x7e]{1,128}\z")]
    private static partial Regex AccessKeyIdPattern();
}

/// <summary>Where and how to put an archive.</summary>
public sealed record BackupDestination(string Endpoint, string Bucket, string Region, string AccessKeyId, string SecretAccessKey);

/// <summary>The typed view of the section.</summary>
/// <param name="Problems">What stops a backup, in words for the administrator; empty when it can run.</param>
public sealed record BackupSettings(
    bool Enabled,
    TimeSpan Interval,
    string? Recipient,
    string Prefix,
    BackupDestination? Destination,
    IReadOnlyList<string> Problems)
{
    public static BackupSettings From(ResolvedSettings settings)
    {
        var problems = settings.All.Where(field => field.Problem is not null)
            .Select(field => $"{field.Field.Name}: {field.Problem}")
            .ToList();

        string? Required(string name)
        {
            var field = settings[name];
            if (!field.HasValue && field.Problem is null)
            {
                problems.Add($"{name} is not set.");
            }

            return field.HasValue ? field.Value : null;
        }

        var recipient = Required(BackupSettingsSection.RecipientField);
        var endpoint = Required(BackupSettingsSection.EndpointField);
        var bucket = Required(BackupSettingsSection.BucketField);
        var accessKeyId = Required(BackupSettingsSection.AccessKeyIdField);
        var secret = Required(BackupSettingsSection.SecretAccessKeyField);
        var region = settings[BackupSettingsSection.RegionField].Value ?? BackupSettingsSection.DefaultRegion;
        var hours = int.TryParse(settings[BackupSettingsSection.IntervalHoursField].Value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BackupSettingsSection.DefaultIntervalHours;

        return new BackupSettings(
            settings[BackupSettingsSection.EnabledField].Value == "true",
            TimeSpan.FromHours(hours),
            recipient,
            settings[BackupSettingsSection.PrefixField].Value ?? "",
            endpoint is not null && bucket is not null && accessKeyId is not null && secret is not null
                ? new BackupDestination(endpoint, bucket, region, accessKeyId, secret)
                : null,
            problems);
    }
}
