using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Audit;

/// <summary>Where audit entries are kept.</summary>
public interface IAuditLog
{
    /// <summary>
    /// Writes the entry right away, on its own, apart from the caller's unit of work: a denied or failed attempt must be
    /// recorded even though the caller's changes are not saved.
    /// </summary>
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Entries matching the filter, newest first, and how many match in total.</summary>
    Task<AuditPage> ListAsync(AuditFilter filter, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Removes the entries that occurred before <paramref name="cutoff"/>; returns how many.</summary>
    Task<int> RemoveOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>Which entries to show; every null field matches everything.</summary>
/// <param name="Search">Part of the actor's or the target's name, case-insensitive.</param>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
public sealed record AuditFilter(
    AuditCategory? Category = null,
    AuditAction? Action = null,
    AuditOutcome? Outcome = null,
    UserId? ActorId = null,
    EnvironmentId? EnvironmentId = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Total);

/// <summary>Where the current request came from; filled by the Api from the HTTP connection.</summary>
public interface IRequestOrigin
{
    /// <summary>The request is the Discord bot acting for a linked user.</summary>
    bool IsBot { get; }

    /// <summary>The client's IP address, after the reverse proxy's forwarded headers; null when unknown.</summary>
    string? Address { get; }
}
