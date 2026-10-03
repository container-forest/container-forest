using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class RepositoryConfiguration : IEntityTypeConfiguration<GitRepository>
{
    public void Configure(EntityTypeBuilder<GitRepository> builder)
    {
        builder.ToTable("Repositories");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        // Not unique (ADR-0006): a check across the whole system would tell editors the names of others' repositories.
        builder.Property(r => r.Name).HasMaxLength(GitRepository.MaxNameLength).UseCollation("NOCASE");
        builder.HasIndex(r => r.Name);

        builder.Property(r => r.Url).HasConversion(url => url.ToString(), value => new Uri(value)).HasMaxLength(2048);

        // A connection in use cannot be deleted: its repositories would turn public and be fetched without the token.
        builder.HasOne<GitConnection>().WithMany().HasForeignKey(r => r.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.ConnectionId);

        builder.Ignore(r => r.HasWebhook);
    }
}

internal sealed class GitConnectionConfiguration : IEntityTypeConfiguration<GitConnection>
{
    public void Configure(EntityTypeBuilder<GitConnection> builder)
    {
        builder.ToTable("GitConnections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasMaxLength(GitConnection.MaxNameLength).UseCollation("NOCASE");
        builder.HasIndex(c => c.Name).IsUnique();

        builder.Property(c => c.UrlPrefix).HasConversion(url => url.ToString(), value => new Uri(value)).HasMaxLength(2048);
        builder.Property(c => c.AccessUserName).HasMaxLength(GitConnection.MaxAccessUserNameLength);
        builder.Ignore(c => c.HasAccessToken);
        builder.Ignore(c => c.HasWebhook);
        builder.Ignore(c => c.AllowedEnvironments);

        builder.OwnsMany<GitConnectionEnvironment>("environments", environments =>
        {
            environments.ToTable("GitConnectionEnvironments");
            environments.WithOwner().HasForeignKey("GitConnectionId");
            environments.HasKey("GitConnectionId", nameof(GitConnectionEnvironment.EnvironmentId));

            // A deleted environment no longer needs the permission.
            environments.HasOne<DockerEnvironment>().WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Navigation("environments").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
