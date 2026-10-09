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

internal sealed class DiscordLinkConfiguration : IEntityTypeConfiguration<DiscordLink>
{
    public void Configure(EntityTypeBuilder<DiscordLink> builder)
    {
        builder.ToTable("DiscordLinks");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasOne<User>().WithOne().HasForeignKey<DiscordLink>(l => l.Id).OnDelete(DeleteBehavior.Cascade);

        // Snowflakes fit in a signed 64-bit integer, which is what SQLite stores.
        builder.Property(l => l.DiscordUserId).HasConversion<long?>();
        builder.HasIndex(l => l.DiscordUserId).IsUnique();
        builder.Property(l => l.DiscordUserName).HasMaxLength(DiscordLink.MaxDiscordUserNameLength);

        builder.Property(l => l.PendingCodeHash).HasMaxLength(EnvironmentConfiguration.Sha256HexLength);
        builder.HasIndex(l => l.PendingCodeHash).IsUnique();

        builder.Ignore(l => l.IsLinked);
    }
}

internal sealed class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.ToTable("ApiTokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.UserId);

        builder.Property(t => t.Name).HasMaxLength(ApiToken.MaxNameLength);
        builder.Property(t => t.TokenHash).HasMaxLength(EnvironmentConfiguration.Sha256HexLength);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.Property(t => t.Hint).HasMaxLength(ApiToken.Prefix.Length + ApiToken.HintLength);

        builder.OwnsMany<ApiTokenEnvironment>("environments", environments =>
        {
            environments.ToTable("ApiTokenEnvironments");
            environments.WithOwner().HasForeignKey("ApiTokenId");
            environments.HasKey("ApiTokenId", nameof(ApiTokenEnvironment.EnvironmentId));

            // A removed environment leaves the token restricted to the rest, or to nothing (ApiToken.Environments).
            environments.HasOne<DockerEnvironment>().WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Navigation("environments").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.Environments);
        builder.Ignore(t => t.Scope);
    }
}

internal sealed class CliAuthorizationConfiguration : IEntityTypeConfiguration<CliAuthorization>
{
    public void Configure(EntityTypeBuilder<CliAuthorization> builder)
    {
        builder.ToTable("CliAuthorizations");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.DeviceCodeHash).HasMaxLength(EnvironmentConfiguration.Sha256HexLength);
        builder.HasIndex(a => a.DeviceCodeHash).IsUnique();
        builder.Property(a => a.UserCodeHash).HasMaxLength(EnvironmentConfiguration.Sha256HexLength);
        builder.HasIndex(a => a.UserCodeHash).IsUnique();
        builder.HasIndex(a => a.ExpiresAt);

        builder.Property(a => a.ClientName).HasMaxLength(CliAuthorization.MaxClientNameLength);
        builder.Property(a => a.HostName).HasMaxLength(CliAuthorization.MaxHostNameLength);
        builder.Property(a => a.RequestedFrom).HasMaxLength(CliAuthorization.MaxAddressLength);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.TokenName).HasMaxLength(ApiToken.MaxNameLength);

        // A deleted user takes their approvals along.
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.ApprovedBy).OnDelete(DeleteBehavior.Cascade);

        builder.OwnsMany<CliAuthorizationEnvironment>("tokenEnvironments", environments =>
        {
            environments.ToTable("CliAuthorizationEnvironments");
            environments.WithOwner().HasForeignKey("CliAuthorizationId");
            environments.HasKey("CliAuthorizationId", nameof(CliAuthorizationEnvironment.EnvironmentId));
            environments.HasOne<DockerEnvironment>().WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Navigation("tokenEnvironments").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(a => a.TokenEnvironments);
    }
}
