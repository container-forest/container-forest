using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Common;

/// <summary>Loads and stores entities of one type. Changes are written by <see cref="IUnitOfWork"/>.</summary>
public interface IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    Task<TEntity?> FindAsync(TId id, CancellationToken cancellationToken);

    /// <summary>Like <see cref="FindAsync"/>, but a missing entity is an error.</summary>
    Task<TEntity> GetAsync(TId id, CancellationToken cancellationToken);

    void Add(TEntity entity);

    void Remove(TEntity entity);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
