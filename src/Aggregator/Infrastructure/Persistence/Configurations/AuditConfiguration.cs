using ContainerForest.Aggregator.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // By name: adding an action in the middle of the enum must not change what old entries say.
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Category).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Channel).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.TargetKind).HasConversion<string>().HasMaxLength(16);

        builder.Property(e => e.ActorName).HasMaxLength(AuditEntry.MaxNameLength);
        builder.Property(e => e.Address).HasMaxLength(AuditEntry.MaxAddressLength);
        builder.Property(e => e.TargetId).HasMaxLength(AuditEntry.MaxNameLength);
        builder.Property(e => e.TargetName).HasMaxLength(AuditEntry.MaxNameLength);
        builder.Property(e => e.Details).HasMaxLength(AuditEntry.MaxDetailsLength);

        // The log outlives what it describes: ids are plain values, without foreign keys.
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.ActorId, e.OccurredAt });
        builder.HasIndex(e => new { e.EnvironmentId, e.OccurredAt });
        builder.HasIndex(e => new { e.Category, e.OccurredAt });
    }
}
