using ContainerForest.Aggregator.Domain.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class AgeKeyConfiguration : IEntityTypeConfiguration<AgeKey>
{
    public void Configure(EntityTypeBuilder<AgeKey> builder)
    {
        builder.ToTable("AgeKeys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();

        // Keys die with their environment: nothing else could decrypt with them.
        builder.HasOne<DockerEnvironment>().WithMany().HasForeignKey(k => k.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(k => k.EnvironmentId);

        builder.Property(k => k.Recipient).HasMaxLength(62);
        builder.HasIndex(k => k.Recipient).IsUnique();
        builder.Property(k => k.ProtectedIdentity).HasMaxLength(8192);
    }
}
