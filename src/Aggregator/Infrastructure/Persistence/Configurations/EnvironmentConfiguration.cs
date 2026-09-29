using ContainerForest.Aggregator.Domain.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class EnvironmentConfiguration : IEntityTypeConfiguration<DockerEnvironment>
{
    private const int Sha256HexLength = 64;

    public void Configure(EntityTypeBuilder<DockerEnvironment> builder)
    {
        builder.ToTable("Environments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Name).HasMaxLength(DockerEnvironment.MaxNameLength).UseCollation("NOCASE");
        builder.HasIndex(e => e.Name).IsUnique();

        builder.Property(e => e.EnrollmentTokenHash).HasMaxLength(Sha256HexLength);
        builder.HasIndex(e => e.EnrollmentTokenHash).IsUnique();
        builder.Property(e => e.AgentSecretHash).HasMaxLength(Sha256HexLength);

        builder.Property(e => e.AgentHostname).HasMaxLength(DockerEnvironment.MaxHostnameLength);
        builder.Property(e => e.AgentVersion).HasMaxLength(64);
        builder.Property(e => e.LastBootId).HasMaxLength(64);

        builder.Ignore(e => e.IsEnrolled);
    }
}
