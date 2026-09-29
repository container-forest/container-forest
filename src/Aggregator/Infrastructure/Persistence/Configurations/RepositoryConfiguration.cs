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

        builder.Property(r => r.Name).HasMaxLength(GitRepository.MaxNameLength).UseCollation("NOCASE");
        builder.HasIndex(r => r.Name).IsUnique();

        builder.Property(r => r.Url).HasConversion(url => url.ToString(), value => new Uri(value)).HasMaxLength(2048);
    }
}
