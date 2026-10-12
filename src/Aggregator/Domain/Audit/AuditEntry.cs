using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Audit;

/// <summary>How the request reached the aggregator.</summary>
public enum AuditChannel
{
    /// <summary>A session in the panel.</summary>
    Panel,

    /// <summary>An API token: the CLI or a script.</summary>
    ApiToken,

    /// <summary>The Discord bot, on behalf of a linked user.</summary>
    Bot,

    /// <summary>No session yet, e.g. a sign-in attempt.</summary>
    Anonymous,
}

/// <summary>Who acted: the user as they were named at the time, and how they came.</summary>
/// <param name="UserId">Null when nobody is signed in, e.g. a failed sign-in with an unknown name.</param>
/// <param name="UserName">The user's name at the time, or the name a sign-in attempt gave.</param>
/// <param name="Address">The client's IP address; null for the bot, whose address says nothing about the person.</param>
public sealed record AuditActor(UserId? UserId, string? UserName, AuditChannel Channel, ApiTokenId? ApiTokenId, string? Address);

/// <summary>
/// One line of the audit log: who did what to what, and how it ended. Entries are written once and never change.
/// </summary>
/// <remarks>
/// Names are copied, not referenced: the log has no foreign keys and must stay readable after users, stacks and
/// environments are renamed or removed. No secret ever goes into an entry (security invariant 6): passwords, tokens and
/// file contents are not part of any field, only names and paths.
/// </remarks>
public sealed class AuditEntry : Entity<AuditEntryId>
{
    public const int MaxNameLength = 200;
    public const int MaxDetailsLength = 500;
    public const int MaxAddressLength = 64;

    private AuditEntry(AuditEntryId id, AuditActor actor, AuditAction action, AuditOutcome outcome, AuditTarget? target, string? details, DateTimeOffset occurredAt)
        : base(id)
    {
        OccurredAt = occurredAt;
        Action = action;
        Category = AuditActions.CategoryOf(action);
        Outcome = outcome;
        ActorId = actor.UserId;
        ActorName = Clip(actor.UserName, MaxNameLength);
        Channel = actor.Channel;
        ApiTokenId = actor.ApiTokenId;
        Address = Clip(actor.Address, MaxAddressLength);
        TargetKind = target?.Kind;
        TargetId = Clip(target?.Id, MaxNameLength);
        TargetName = Clip(target?.Name, MaxNameLength);
        EnvironmentId = target?.EnvironmentId;
        StackId = target?.StackId;
        Details = Clip(details, MaxDetailsLength);
    }

    // For EF Core.
    private AuditEntry() { }

    public DateTimeOffset OccurredAt { get; private init; }

    public AuditAction Action { get; private init; }

    /// <summary>Derived from <see cref="Action"/>; stored so the log can be filtered by it.</summary>
    public AuditCategory Category { get; private init; }

    public AuditOutcome Outcome { get; private init; }

    public UserId? ActorId { get; private init; }

    public string? ActorName { get; private init; }

    public AuditChannel Channel { get; private init; }

    public ApiTokenId? ApiTokenId { get; private init; }

    public string? Address { get; private init; }

    public AuditTargetKind? TargetKind { get; private init; }

    public string? TargetId { get; private init; }

    public string? TargetName { get; private init; }

    public EnvironmentId? EnvironmentId { get; private init; }

    public StackId? StackId { get; private init; }

    /// <summary>What else matters: the new role, why it failed. Never a secret.</summary>
    public string? Details { get; private init; }

    public static AuditEntry Record(
        AuditActor actor, AuditAction action, AuditOutcome outcome, AuditTarget? target, string? details, DateTimeOffset now)
    {
        if (!Enum.IsDefined(action) || !Enum.IsDefined(outcome) || !Enum.IsDefined(actor.Channel))
        {
            throw new DomainValidationException("Unknown audit action, outcome or channel.");
        }

        return new AuditEntry(AuditEntryId.New(), actor, action, outcome, target, details, now);
    }

    /// <summary>Trims, turns line breaks into spaces (one entry is one line) and cuts to <paramref name="max"/> characters.</summary>
    private static string? Clip(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > max ? string.Concat(cleaned.AsSpan(0, max - 1), "…") : cleaned;
    }
}
