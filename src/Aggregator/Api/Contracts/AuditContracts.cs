using ContainerForest.Aggregator.Domain.Audit;

namespace ContainerForest.Aggregator.Api.Contracts;

// The audit log for the panel: administrators only (403 for anyone else, and for a token limited to some environments).
//
//   GET /api/audit?category=&action=&outcome=&userId=&environmentId=&search=&from=&to=&page=&pageSize=
//       -> AuditPageResponse, newest first
//
// - Every filter is optional. `search` matches part of the actor's or the target's name. `from` is inclusive, `to`
//   exclusive. `page` starts at 1; `pageSize` is 1-200, 50 by default.
// - Entries keep names as they were: a renamed or removed user, stack or environment reads as it did then.
// - No secret is ever part of an entry: passwords, tokens and file contents are not recorded, only names and paths.

/// <param name="ActorId">Null when nobody was signed in, e.g. a failed sign-in with an unknown name.</param>
/// <param name="ActorName">The user's name at the time, or the name a sign-in attempt gave.</param>
/// <param name="Address">The client's IP address; null for the Discord bot.</param>
/// <param name="TargetId">The object's id as text; for a container, its name.</param>
/// <param name="Details">E.g. the new role, or why the action failed or was denied.</param>
public sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    AuditAction Action,
    AuditCategory Category,
    AuditOutcome Outcome,
    Guid? ActorId,
    string? ActorName,
    AuditChannel Channel,
    string? Address,
    AuditTargetKind? TargetKind,
    string? TargetId,
    string? TargetName,
    Guid? EnvironmentId,
    Guid? StackId,
    string? Details);

/// <param name="Total">How many entries match the filter, across all pages.</param>
public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Entries, int Total, int Page, int PageSize);
