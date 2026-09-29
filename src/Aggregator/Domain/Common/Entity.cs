namespace ContainerForest.Aggregator.Domain.Common;

/// <summary>Base class for entities: two entities are equal when they have the same type and id.</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IStronglyTypedId<TId>
{
    protected Entity(TId id) => Id = id;

    // For EF Core.
    protected Entity() { }

    public TId Id { get; private init; }

    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && other.Id.Equals(Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
