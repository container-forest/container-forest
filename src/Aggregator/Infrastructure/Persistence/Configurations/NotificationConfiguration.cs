using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        builder.ToTable("NotificationChannels");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasMaxLength(NotificationChannel.MaxNameLength).UseCollation("NOCASE");
        builder.HasIndex(c => c.Name).IsUnique();
        builder.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);

        // Snowflakes fit in a signed 64-bit integer, which is what SQLite stores.
        builder.Property(c => c.DiscordChannelId).HasConversion<long>();
    }
}

internal sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> builder)
    {
        builder.ToTable("NotificationRules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        // Flags as an integer: rules are matched in memory, so the set does not need its own table.
        builder.Property(r => r.Events).HasConversion<int>();

        builder.HasOne<NotificationChannel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DockerEnvironment>().WithMany().HasForeignKey(r => r.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Stack>().WithMany().HasForeignKey(r => r.StackId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();

        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(n => n.Severity).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.Summary).HasMaxLength(Notification.MaxSummaryLength);
        builder.Property(n => n.Error).HasMaxLength(Notification.MaxErrorLength);
        builder.Property(n => n.Details).HasConversion<NotificationDetailsConverter>();

        builder.HasIndex(n => new { n.Status, n.CreatedAt });

        // History outlives rules and channels: the ids stay as plain values, without foreign keys.
        builder.Ignore(n => n.IsFinished);
    }
}
