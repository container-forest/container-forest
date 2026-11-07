using ContainerForest.Aggregator.Domain.Backups;
using ContainerForest.Aggregator.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class StoredSettingConfiguration : IEntityTypeConfiguration<StoredSetting>
{
    public void Configure(EntityTypeBuilder<StoredSetting> builder)
    {
        builder.ToTable("StoredSettings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Section).HasMaxLength(StoredSetting.MaxNameLength);
        builder.Property(s => s.Field).HasMaxLength(StoredSetting.MaxNameLength);
        builder.HasIndex(s => new { s.Section, s.Field }).IsUnique();
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        builder.ToTable("BackupRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.ObjectKey).HasMaxLength(BackupRun.MaxObjectKeyLength);
        builder.Property(r => r.Error).HasMaxLength(BackupRun.MaxErrorLength);
        builder.HasIndex(r => r.FinishedAt);
    }
}
