using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>
/// The audit log in the aggregator's database. Entries are written through a context of their own, so that recording
/// a failed action does not save the half-made changes of the request's context with it.
/// </summary>
internal sealed class AuditLog(AggregatorDbContext db, IServiceScopeFactory scopes) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AggregatorDbContext>();
        context.AuditEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuditPage> ListAsync(AuditFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        var query = Apply(db.AuditEntries.AsNoTracking(), filter);
        var total = await query.CountAsync(cancellationToken);

        // Ids are version 7 GUIDs; stored as text they sort in creation order, which settles entries of the same instant.
        var entries = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return new AuditPage(entries, total);
    }

    public Task<int> RemoveOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        db.AuditEntries.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    private static IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> query, AuditFilter filter)
    {
        if (filter.Category is { } category)
        {
            query = query.Where(e => e.Category == category);
        }

        if (filter.Action is { } action)
        {
            query = query.Where(e => e.Action == action);
        }

        if (filter.Outcome is { } outcome)
        {
            query = query.Where(e => e.Outcome == outcome);
        }

        if (filter.ActorId is { } actorId)
        {
            query = query.Where(e => e.ActorId == actorId);
        }

        if (filter.EnvironmentId is { } environmentId)
        {
            query = query.Where(e => e.EnvironmentId == environmentId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        if (filter.Search is { } search)
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(e =>
                EF.Functions.Like(e.ActorName!, pattern, "\\") || EF.Functions.Like(e.TargetName!, pattern, "\\"));
        }

        return query;
    }
}
