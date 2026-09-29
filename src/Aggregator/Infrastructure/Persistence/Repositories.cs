using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using Microsoft.EntityFrameworkCore;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

internal class EfRepository<TEntity, TId>(AggregatorDbContext db) : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    protected AggregatorDbContext Db { get; } = db;

    protected DbSet<TEntity> Set => Db.Set<TEntity>();

    public async Task<TEntity?> FindAsync(TId id, CancellationToken cancellationToken) =>
        await Set.FindAsync([id], cancellationToken);

    public async Task<TEntity> GetAsync(TId id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException(typeof(TEntity).Name, id);

    public void Add(TEntity entity) => Set.Add(entity);

    public void Remove(TEntity entity) => Set.Remove(entity);
}

internal sealed class EnvironmentRepository(AggregatorDbContext db)
    : EfRepository<DockerEnvironment, EnvironmentId>(db), IEnvironmentRepository
{
    public Task<DockerEnvironment?> FindByEnrollmentTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(e => e.EnrollmentTokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<DockerEnvironment>> ListAsync(CancellationToken cancellationToken) =>
        await Set.OrderBy(e => e.Name).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        Set.AnyAsync(e => e.Name == name, cancellationToken);
}
