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
        builder.Property(d => d.ApprovedCommit)
            .HasConversion(sha => sha!.Value, value => CommitSha.Parse(value))
            .HasMaxLength(64);
        builder.Property(d => d.Endorsement).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.EndorsementProblem).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.SignerName).HasMaxLength(User.MaxUserNameLength);
        builder.Property(d => d.SigningKeyFingerprint).HasMaxLength(SigningKeyConfiguration.FingerprintLength);

        builder.HasIndex(d => new { d.StackId, d.QueuedAt });

        // Removing a stack takes its history with it (StackService.RemoveAsync); deployments never outlive their stack otherwise.
        builder.HasOne<Stack>().WithMany().HasForeignKey(d => d.StackId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.RequestedBy).OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(d => d.Log, log =>
        {
            log.ToTable("DeploymentLogLines");
            log.WithOwner().HasForeignKey("DeploymentId");
            log.HasKey("DeploymentId", nameof(DeploymentLogLine.Sequence));
            log.Property(l => l.Sequence).ValueGeneratedNever();
            log.Property(l => l.Level).HasConversion<string>().HasMaxLength(16);
            log.Property(l => l.Text).HasMaxLength(Deployment.MaxLogLineLength);
        });
        builder.Navigation(d => d.Log).UsePropertyAccessMode(PropertyAccessMode.Field);

        // No foreign key: the signer's name is kept, and history outlives the user.
        builder.Ignore(d => d.IsAutomatic);
        builder.Ignore(d => d.IsFinished);
        builder.Ignore(d => d.Duration);
    }
}
