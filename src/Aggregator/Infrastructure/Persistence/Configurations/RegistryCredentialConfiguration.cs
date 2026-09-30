using ContainerForest.Aggregator.Domain.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class RegistryCredentialConfiguration : IEntityTypeConfiguration<RegistryCredential>
{
    public void Configure(EntityTypeBuilder<RegistryCredential> builder)
    {
        builder.ToTable("RegistryCredentials");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.HasOne<DockerEnvironment>().WithMany().HasForeignKey(c => c.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(c => c.Server).HasMaxLength(260);
        builder.HasIndex(c => new { c.EnvironmentId, c.Server }).IsUnique();

        builder.Property(c => c.Username).HasMaxLength(RegistryCredential.MaxUsernameLength);
        builder.Property(c => c.ProtectedPassword).HasMaxLength(8192);
    }
}
