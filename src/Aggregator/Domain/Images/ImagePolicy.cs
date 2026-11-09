using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Images;

/// <summary>How a policy tells which of two tags is newer (ADR-0015).</summary>
public enum ImageTagOrder
{
    /// <summary>The "created" time in the image's configuration: works without changes in CI, costs registry requests.</summary>
    ImageCreated,

    /// <summary>The named group "ts" of the tag pattern: no requests beyond the tag list, but the tags must carry it.</summary>
    TagTimestamp,
}

/// <summary>
/// Lets the aggregator update one image of a stack (ADR-0015): it reads the registry, picks the newest tag the pattern
/// allows and commits it to the stack's compose file. An Editor of the stack creates it; nothing about it reaches
/// beyond the stack's own compose file.
/// </summary>
public sealed partial class ImagePolicy : Entity<ImagePolicyId>
{
    public const int MaxTagPatternLength = 256;
    public const int MaxServices = 32;
    public const int MaxErrorLength = 1000;
    public const int MaxPinnedByLength = 128;

    /// <summary>Policies one stack may have: each costs registry requests on every check.</summary>
    public const int MaxPerStack = 10;

    /// <summary>The name of the group a <see cref="ImageTagOrder.TagTimestamp"/> pattern must have.</summary>
    public const string TimestampGroup = "ts";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private List<string> services = [];

    private ImagePolicy(ImagePolicyId id, StackId stackId, ImageName image, DateTimeOffset createdAt)
        : base(id)
    {
        StackId = stackId;
        Image = image;
        TagPattern = "";
        CreatedAt = createdAt;
    }

    // For EF Core.
    private ImagePolicy()
    {
        Image = null!;
        TagPattern = null!;
    }

    public StackId StackId { get; private init; }

    public ImageName Image { get; private init; }

    /// <summary>A regular expression a tag must match whole, from '^' to '\z'.</summary>
    public string TagPattern { get; private set; }

    public ImageTagOrder Order { get; private set; }

    /// <summary>Only these services are updated; empty for every service with the image.</summary>
    public IReadOnlyList<string> Services => services;

    /// <summary>The aggregator reads the registry on its own every polling interval.</summary>
    public bool Poll { get; private set; }

    /// <summary>The stack's image hook may ask for a check now or for an exact tag.</summary>
    public bool AcceptRequests { get; private set; }

    /// <summary>Writes "tag@sha256:…" instead of the tag alone; required when the stack requires image digests.</summary>
    public bool PinDigest { get; private set; }

    /// <summary>The tag automatic updates are held at; null when they run.</summary>
    public string? PinnedTag { get; private set; }

    /// <summary>Who pinned it: a user name, or the image hook.</summary>
    public string? PinnedBy { get; private set; }

    public DateTimeOffset? PinnedAt { get; private set; }

    public bool IsPinned => PinnedTag is not null;

    public UserId LastChangedBy { get; private set; }

    /// <summary>
    /// An administrator of the system or of the stack's environment made the last change: only then do update commits keep
    /// the compose policy exemption of the content under them (ADR-0015, item 7).
    /// </summary>
    public bool LastChangedByAdministrator { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Why the last check did not finish; null after a successful one.</summary>
    public string? LastError { get; private set; }

    /// <summary>The failure in <see cref="LastError"/> was notified; cleared by a successful check, so one failure is told once.</summary>
    public bool FailureNotified { get; private set; }

    /// <summary>Checks in a row the registry could not be read; the third one is a failure worth a notification.</summary>
    public int RegistryFailures { get; private set; }

    /// <summary>Something worth knowing about the policy that does not stop it, e.g. that its order is expensive here.</summary>
    public string? Warning { get; private set; }

    /// <summary>The tag the last check found newest; null before one did.</summary>
    public string? LatestTag { get; private set; }

    /// <exception cref="DomainValidationException">The pattern or the services are not valid.</exception>
    public static ImagePolicy Create(
        StackId stackId,
        ImageName image,
        ImagePolicySettings settings,
        UserId changedBy,
        bool byAdministrator,
        DateTimeOffset now)
    {
        var policy = new ImagePolicy(ImagePolicyId.New(), stackId, image, now);
        policy.Change(settings, changedBy, byAdministrator);
        return policy;
    }

    /// <exception cref="DomainValidationException">The pattern or the services are not valid.</exception>
    public void Change(ImagePolicySettings settings, UserId changedBy, bool byAdministrator)
    {
        var pattern = ValidatePattern(settings.TagPattern, settings.Order);
        var names = NormalizeServices(settings.Services);
        if (PinnedTag is not null && !pattern.IsMatch(PinnedTag))
        {
            throw new DomainValidationException($"The pinned tag '{PinnedTag}' does not match the new pattern; unpin it first.");
        }

        TagPattern = settings.TagPattern;
        Order = settings.Order;
        services = names;
        Poll = settings.Poll;
        AcceptRequests = settings.AcceptRequests;
        PinDigest = settings.PinDigest;
        LastChangedBy = changedBy;
        LastChangedByAdministrator = byAdministrator;
    }

    public ImagePolicySettings Settings => new(TagPattern, Order, services, Poll, AcceptRequests, PinDigest);

    /// <summary>True when the tag matches the pattern whole.</summary>
    public bool Allows(string tag) => IsValidTag(tag) && Pattern().IsMatch(tag);

    /// <summary>The value of the "ts" group for a <see cref="ImageTagOrder.TagTimestamp"/> policy.</summary>
    public bool TryReadTimestamp(string tag, [NotNullWhen(true)] out string? timestamp)
    {
        timestamp = null;
        if (Order != ImageTagOrder.TagTimestamp || !IsValidTag(tag))
        {
            return false;
        }

        var match = Pattern().Match(tag);
        if (!match.Success || match.Groups[TimestampGroup] is not { Success: true, Length: > 0 } group)
        {
            return false;
        }

        timestamp = group.Value;
        return true;
    }

    /// <summary>
    /// Compares two timestamps from tags: the longer is newer, equal lengths compare as text. Numbers without leading
    /// zeros and fixed formats such as 20261109T120000 both order correctly this way.
    /// </summary>
    public static int CompareTimestamps(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    /// <summary>True when this policy updates the service: by its image and, when the policy lists services, by its name.</summary>
    public bool Covers(string serviceName) => services.Count == 0 || services.Contains(serviceName, StringComparer.Ordinal);

    /// <summary>Holds automatic updates at the tag.</summary>
    /// <param name="by">A user name, or the image hook.</param>
    /// <exception cref="DomainValidationException">The tag does not match the pattern.</exception>
    public void Pin(string tag, string by, DateTimeOffset now)
    {
        if (!Allows(tag))
        {
            throw new DomainValidationException($"Tag '{tag}' does not match the policy's pattern {TagPattern}.");
        }

        PinnedTag = tag;
        PinnedBy = by.Length > MaxPinnedByLength ? by[..MaxPinnedByLength] : by;
        PinnedAt = now;
    }

    public void Unpin()
    {
        PinnedTag = null;
        PinnedBy = null;
        PinnedAt = null;
    }

    /// <summary>A check finished: the policy's state is clear again, and the next failure is told again.</summary>
    /// <param name="latestTag">The newest tag the check found; null keeps the previous one.</param>
    /// <param name="warning">Replaces the previous warning; null clears it.</param>
    public void RecordChecked(DateTimeOffset now, string? latestTag, string? warning)
    {
        LastCheckedAt = now;
        LastError = null;
        FailureNotified = false;
        RegistryFailures = 0;
        LatestTag = latestTag ?? LatestTag;
        RecordWarning(warning);
    }

    public void RecordWarning(string? warning) => Warning = warning is { Length: > MaxErrorLength } ? warning[..MaxErrorLength] : warning;

    /// <summary>A check failed.</summary>
    /// <param name="registryUnavailable">The registry could not be read; such failures count only from the third in a row.</param>
    /// <returns>True when the failure should be notified now: the first one since a successful check.</returns>
    public bool RecordFailed(string error, bool registryUnavailable, DateTimeOffset now)
    {
        LastCheckedAt = now;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        RegistryFailures = registryUnavailable ? RegistryFailures + 1 : 0;
        if (FailureNotified || (registryUnavailable && RegistryFailures < 3))
        {
            return false;
        }

        FailureNotified = true;
        return true;
    }

    /// <summary>True for a tag the registry would accept (OCI distribution: up to 128 word characters, '.' and '-').</summary>
    public static bool IsValidTag([NotNullWhen(true)] string? tag) => tag is not null && TagFormat().IsMatch(tag);

    private Regex Pattern() => new(TagPattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);

    private static Regex ValidatePattern(string? pattern, ImageTagOrder order)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxTagPatternLength)
        {
            throw new DomainValidationException($"The tag pattern must be 1-{MaxTagPatternLength} characters.");
        }

        // Anchored at both ends, with \z rather than $, which lets a trailing newline through.
        if (!pattern.StartsWith('^') || !pattern.EndsWith(@"\z", StringComparison.Ordinal) || pattern.EndsWith(@"\\z", StringComparison.Ordinal))
        {
            throw new DomainValidationException(@"The tag pattern must start with '^' and end with '\z', e.g. ^main-[0-9a-f]{7}\z.");
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new DomainValidationException($"The tag pattern is not a valid regular expression: {ex.Message}");
        }

        var hasTimestamp = regex.GetGroupNames().Contains(TimestampGroup, StringComparer.Ordinal);
        if (order == ImageTagOrder.TagTimestamp && !hasTimestamp)
        {
            throw new DomainValidationException(
                $"Ordering by the tag's timestamp needs a named group (?<{TimestampGroup}>…) in the pattern, " +
                @"e.g. ^main-(?<ts>[0-9]{8}T[0-9]{6})-[0-9a-f]{7}\z.");
        }

        return regex;
    }

    private static List<string> NormalizeServices(IEnumerable<string>? names)
    {
        var result = (names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (result.Count > MaxServices || result.Any(n => !ServiceName().IsMatch(n)))
        {
            throw new DomainValidationException(
                $"Services are up to {MaxServices} compose service names (letters, digits, '.', '_' and '-').");
        }

        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagFormat();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceName();
}

/// <summary>What an Editor sets on a policy; the image itself never changes, a policy for another image is a new one.</summary>
public sealed record ImagePolicySettings(
    string TagPattern,
    ImageTagOrder Order,
    IReadOnlyList<string> Services,
    bool Poll = true,
    bool AcceptRequests = true,
    bool PinDigest = false);
