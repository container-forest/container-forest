using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("Deployments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.Trigger).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.PullPolicy).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.Commit)
            .HasConversion(sha => sha!.Value, value => CommitSha.Parse(value))
            .HasMaxLength(64);
        builder.Property(d => d.Error).HasMaxLength(Deployment.MaxErrorLength);

        builder.HasIndex(d => new { d.StackId, d.QueuedAt });

        builder.HasOne<Stack>().WithMany().HasForeignKey(d => d.StackId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.RequestedBy).OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(d => d.IsFinished);
        builder.Ignore(d => d.Duration);
    }
}
