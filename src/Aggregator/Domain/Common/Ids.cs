namespace ContainerForest.Aggregator.Domain.Common;

/// <summary>
/// Id wrapper over <see cref="Guid"/> that keeps ids of different entities from being mixed up.
/// Generic code (EF Core converters, route binding) creates ids through <see cref="From"/>.
/// </summary>
public interface IStronglyTypedId<TSelf> : IEquatable<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

public readonly record struct EnvironmentId(Guid Value) : IStronglyTypedId<EnvironmentId>
{
    public static EnvironmentId New() => new(Guid.CreateVersion7());
    public static EnvironmentId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct RepositoryId(Guid Value) : IStronglyTypedId<RepositoryId>
{
    public static RepositoryId New() => new(Guid.CreateVersion7());
    public static RepositoryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct StackId(Guid Value) : IStronglyTypedId<StackId>
{
    public static StackId New() => new(Guid.CreateVersion7());
    public static StackId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct DeploymentId(Guid Value) : IStronglyTypedId<DeploymentId>
{
    public static DeploymentId New() => new(Guid.CreateVersion7());
    public static DeploymentId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value) : IStronglyTypedId<UserId>
{
    public static UserId New() => new(Guid.CreateVersion7());
    public static UserId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct RoleAssignmentId(Guid Value) : IStronglyTypedId<RoleAssignmentId>
{
    public static RoleAssignmentId New() => new(Guid.CreateVersion7());
    public static RoleAssignmentId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
