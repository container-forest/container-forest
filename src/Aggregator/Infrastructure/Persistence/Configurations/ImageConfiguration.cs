using System.Text.Json;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class ImagePolicyConfiguration : IEntityTypeConfiguration<ImagePolicy>
{
    public void Configure(EntityTypeBuilder<ImagePolicy> builder)
    {
        builder.ToTable("ImagePolicies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Image)
            .HasConversion(image => image.Value, value => ImageName.Parse(value))
            .HasMaxLength(ImageName.MaxLength + 8);
        builder.HasIndex(p => new { p.StackId, p.Image }).IsUnique();

        builder.Property(p => p.TagPattern).HasMaxLength(ImagePolicy.MaxTagPatternLength);
        builder.Property(p => p.Order).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Services).HasField("services").StoredAsJsonList();
        builder.Property(p => p.PinnedTag).HasMaxLength(128);
        builder.Property(p => p.PinnedBy).HasMaxLength(ImagePolicy.MaxPinnedByLength);
        builder.Property(p => p.LastError).HasMaxLength(ImagePolicy.MaxErrorLength);
        builder.Property(p => p.Warning).HasMaxLength(ImagePolicy.MaxErrorLength);
        builder.Property(p => p.LatestTag).HasMaxLength(128);
        builder.Ignore(p => p.IsPinned);
        builder.Ignore(p => p.Settings);

        // A policy belongs to its stack, and goes with it.
        builder.HasOne<Stack>().WithMany().HasForeignKey(p => p.StackId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ImageUpdateRecordConfiguration : IEntityTypeConfiguration<ImageUpdateRecord>
{
    private static readonly ValueConverter<List<ImageChange>, string> ChangesConverter = new(
        changes => JsonSerializer.Serialize(changes, JsonSerializerOptions.Default),
        json => JsonSerializer.Deserialize<List<ImageChange>>(json, JsonSerializerOptions.Default) ?? new List<ImageChange>());

    private static readonly ValueComparer<List<ImageChange>> ChangesComparer = new(
        (a, b) => a!.SequenceEqual(b!),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        list => list.ToList());

    public void Configure(EntityTypeBuilder<ImageUpdateRecord> builder)
    {
        builder.ToTable("ImageUpdateRecords");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Commit)
            .HasConversion(sha => sha.Value, value => CommitSha.Parse(value))
            .HasMaxLength(64);
        builder.Property(r => r.Parent)
            .HasConversion(sha => sha.Value, value => CommitSha.Parse(value))
            .HasMaxLength(64);
        builder.HasIndex(r => new { r.StackId, r.Commit }).IsUnique();

        builder.Property(r => r.ComposePath)
            .HasConversion(path => path.Value, value => StackFilePath.Parse(value))
            .HasMaxLength(StackFilePath.MaxLength);
        builder.Property(r => r.Tag).HasMaxLength(128);
        builder.Property(r => r.Digest).HasMaxLength(ImageTagCacheEntry.MaxDigestLength);
        builder.Property(r => r.Reason).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.RequestedByName).HasMaxLength(ImageUpdateRecord.MaxNameLength);

        builder.Property<List<ImageChange>>("changes")
            .HasColumnName("Changes")
            .HasConversion(ChangesConverter, ChangesComparer)
            .IsRequired();
        builder.Ignore(r => r.Changes);

        // Records go with the stack: they only endorse its own commits. No foreign key to the policy, which may be
        // removed while its commits are still deployed, nor to the user, whose name is kept.
        builder.HasOne<Stack>().WithMany().HasForeignKey(r => r.StackId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ImageTagCacheEntryConfiguration : IEntityTypeConfiguration<ImageTagCacheEntry>
{
    public void Configure(EntityTypeBuilder<ImageTagCacheEntry> builder)
    {
        builder.ToTable("ImageTagCache");
        builder.HasKey(e => new { e.Image, e.Tag });
        builder.Property(e => e.Image).HasMaxLength(ImageName.MaxLength + 8);
        builder.Property(e => e.Tag).HasMaxLength(128);
        builder.Property(e => e.Digest).HasMaxLength(ImageTagCacheEntry.MaxDigestLength);
    }
}

internal sealed class AggregatorSigningKeyConfiguration : IEntityTypeConfiguration<AggregatorSigningKey>
{
    public void Configure(EntityTypeBuilder<AggregatorSigningKey> builder)
    {
        builder.ToTable("AggregatorSigningKeys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.PublicKey).HasMaxLength(512);
        builder.Property(k => k.Fingerprint).HasMaxLength(SigningKeyConfiguration.FingerprintLength);
        builder.HasIndex(k => k.Fingerprint).IsUnique();
        builder.Ignore(k => k.IsActive);
    }
}
