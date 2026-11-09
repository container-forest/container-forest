using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

internal sealed class ImagePolicyRepository(AggregatorDbContext db)
    : EfRepository<ImagePolicy, ImagePolicyId>(db), IImagePolicyRepository
{
    public async Task<IReadOnlyList<ImagePolicy>> ListByStackAsync(StackId stackId, CancellationToken cancellationToken) =>
        (await Set.Where(p => p.StackId == stackId).ToListAsync(cancellationToken))
            .OrderBy(p => p.Image.Value, StringComparer.Ordinal)
            .ToList();

    public async Task<IReadOnlyList<ImagePolicy>> ListPolledAsync(CancellationToken cancellationToken) =>
        await Set.Where(p => p.Poll && p.PinnedTag == null).ToListAsync(cancellationToken);
}

internal sealed class ImageUpdateRecordRepository(AggregatorDbContext db)
    : EfRepository<ImageUpdateRecord, ImageUpdateRecordId>(db), IImageUpdateRecordRepository
{
    public Task<ImageUpdateRecord?> FindByCommitAsync(StackId stackId, CommitSha commit, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(r => r.StackId == stackId && r.Commit == commit, cancellationToken);

    public async Task<IReadOnlyList<ImageUpdateRecord>> ListByStackAsync(StackId stackId, int limit, CancellationToken cancellationToken) =>
        await Set.Where(r => r.StackId == stackId).OrderByDescending(r => r.CreatedAt).Take(limit).ToListAsync(cancellationToken);
}

internal sealed class ImageTagCache(AggregatorDbContext db) : IImageTagCache
{
    public async Task<IReadOnlyDictionary<string, ImageTagCacheEntry>> ListAsync(ImageName image, CancellationToken cancellationToken)
    {
        var stored = await db.Set<ImageTagCacheEntry>().Where(e => e.Image == image.Value).ToListAsync(cancellationToken);

        // Entries added in this unit of work are not in the database yet, and a check reads what it has just cached.
        var added = db.ChangeTracker.Entries<ImageTagCacheEntry>()
            .Where(e => e.State == EntityState.Added && e.Entity.Image == image.Value)
            .Select(e => e.Entity);
        var result = new Dictionary<string, ImageTagCacheEntry>(StringComparer.Ordinal);
        foreach (var entry in stored.Concat(added))
        {
            result[entry.Tag] = entry;
        }

        return result;
    }

    public async Task PutAsync(ImageTagCacheEntry entry, CancellationToken cancellationToken)
    {
        var set = db.Set<ImageTagCacheEntry>();
        var existing = set.Local.FirstOrDefault(e => e.Image == entry.Image && e.Tag == entry.Tag)
            ?? await set.FindAsync([entry.Image, entry.Tag], cancellationToken);
        if (existing is null)
        {
            set.Add(entry);
            return;
        }

        db.Entry(existing).CurrentValues.SetValues(entry);
    }
}

internal sealed class AggregatorSigningKeyRepository(AggregatorDbContext db)
    : EfRepository<AggregatorSigningKey, AggregatorSigningKeyId>(db), IAggregatorSigningKeyRepository
{
    public Task<AggregatorSigningKey?> FindActiveAsync(CancellationToken cancellationToken) =>
        Set.Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AggregatorSigningKey>> ListAsync(CancellationToken cancellationToken) =>
        await Set.OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(string fingerprint, CancellationToken cancellationToken) =>
        Set.AnyAsync(k => k.Fingerprint == fingerprint, cancellationToken);
}
