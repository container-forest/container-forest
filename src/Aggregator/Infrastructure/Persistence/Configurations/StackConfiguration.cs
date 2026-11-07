using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class StackConfiguration : IEntityTypeConfiguration<Stack>
{
    public void Configure(EntityTypeBuilder<Stack> builder)
    {
        builder.ToTable("Stacks");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Name)
            .HasConversion(name => name.Value, value => StackName.Parse(value))
            .HasMaxLength(StackName.MaxLength);
        builder.HasIndex(s => new { s.EnvironmentId, s.Name }).IsUnique();

        builder.Property(s => s.Branch)
            .HasConversion(branch => branch.Value, value => BranchName.Parse(value))
            .HasMaxLength(BranchName.MaxLength);
        builder.Property(s => s.ComposePath)
            .HasConversion(path => path.Value, value => StackFilePath.Parse(value))
            .HasMaxLength(StackFilePath.MaxLength);
        builder.Property(s => s.DeployedCommit)
            .HasConversion(sha => sha!.Value, value => CommitSha.Parse(value))
            .HasMaxLength(64);

        builder.Property(s => s.PolicyAllowances).HasField("policyAllowances").StoredAsJsonList().HasDefaultValue(Array.Empty<string>());

        builder.HasOne<DockerEnvironment>().WithMany().HasForeignKey(s => s.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GitRepository>().WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
