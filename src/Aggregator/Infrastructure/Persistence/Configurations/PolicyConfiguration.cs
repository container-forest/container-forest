using System.Text.Json;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class ComposePolicyConfiguration : IEntityTypeConfiguration<ComposePolicy>
{
    public void Configure(EntityTypeBuilder<ComposePolicy> builder)
    {
        builder.ToTable("ComposePolicies");
        builder.HasKey(p => p.EnvironmentId);
        builder.Property(p => p.EnvironmentId).ValueGeneratedNever();
        builder.Property(p => p.Mode).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.DataRoot).HasMaxLength(4096);
        builder.Property(p => p.SystemReadOnlyPaths).HasField("systemReadOnlyPaths").StoredAsJsonList();
        builder.Property(p => p.Allowances).HasField("allowances").StoredAsJsonList();
        builder.Ignore(p => p.ParsedAllowances);

        // The policy goes with its environment.
        builder.HasOne<DockerEnvironment>().WithOne().HasForeignKey<ComposePolicy>(p => p.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Short lists of strings stored as one JSON column: they are read and written with their owner only.</summary>
internal static class JsonListStorage
{
    private static readonly ValueConverter<IReadOnlyList<string>, string> Converter = new(
        list => Serialize(list),
        json => Deserialize(json));

    private static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
        (a, b) => a!.SequenceEqual(b!),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
        list => list.ToList());

    public static PropertyBuilder<IReadOnlyList<string>> StoredAsJsonList(this PropertyBuilder<IReadOnlyList<string>> property) =>
        property
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasConversion(Converter, Comparer)
            .IsRequired();

    private static string Serialize(IReadOnlyList<string> list) => JsonSerializer.Serialize(list);

    // A List, since the entities keep the lists in List fields.
    private static IReadOnlyList<string> Deserialize(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
