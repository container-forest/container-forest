using ContainerForest.Aggregator.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>Stores any <see cref="IStronglyTypedId{TSelf}"/> as its <see cref="Guid"/>.</summary>
public sealed class StronglyTypedIdConverter<TId>() : ValueConverter<TId, Guid>(id => id.Value, value => Create(value))
    where TId : struct, IStronglyTypedId<TId>
{
    // Expression trees cannot call static abstract members directly, so the call goes through a regular method.
    private static TId Create(Guid value) => TId.From(value);
}

internal static class ModelConfigurationBuilderExtensions
{
    public static void AddStronglyTypedId<TId>(this ModelConfigurationBuilder builder)
        where TId : struct, IStronglyTypedId<TId> =>
        builder.Properties<TId>().HaveConversion<StronglyTypedIdConverter<TId>>();
}
