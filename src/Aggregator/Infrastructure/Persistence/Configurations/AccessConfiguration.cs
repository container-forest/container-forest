using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.UserName).HasMaxLength(User.MaxUserNameLength).UseCollation("NOCASE");
        builder.HasIndex(u => u.UserName).IsUnique();
    }
}

/// <summary>Both assignment kinds share one table; the Scope column tells them apart.</summary>
internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public const string EnvironmentScope = "Environment";
    public const string StackScope = "Stack";

    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.HasDiscriminator<string>("Scope")
            .HasValue<EnvironmentRoleAssignment>(EnvironmentScope)
            .HasValue<StackRoleAssignment>(StackScope);
        builder.Property("Scope").HasMaxLength(16);

        builder.Property(a => a.Role).HasConversion<string>().HasMaxLength(16);

        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EnvironmentRoleAssignmentConfiguration : IEntityTypeConfiguration<EnvironmentRoleAssignment>
{
    public void Configure(EntityTypeBuilder<EnvironmentRoleAssignment> builder)
    {
        builder.Property(a => a.EnvironmentId).HasColumnName("EnvironmentId");
        builder.HasIndex(a => new { a.UserId, a.EnvironmentId })
            .IsUnique()
            .HasFilter($"\"Scope\" = '{RoleAssignmentConfiguration.EnvironmentScope}'");

        builder.HasOne<DockerEnvironment>().WithMany().HasForeignKey(a => a.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StackRoleAssignmentConfiguration : IEntityTypeConfiguration<StackRoleAssignment>
{
    public void Configure(EntityTypeBuilder<StackRoleAssignment> builder)
    {
        builder.Property(a => a.EnvironmentId).HasColumnName("EnvironmentId");
        builder.HasIndex(a => new { a.UserId, a.StackId }).IsUnique();

        builder.HasOne<Stack>().WithMany().HasForeignKey(a => a.StackId).OnDelete(DeleteBehavior.Cascade);
    }
}
