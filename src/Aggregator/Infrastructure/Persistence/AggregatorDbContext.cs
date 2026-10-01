using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>
/// The aggregator's database, including the Identity tables for sign-in (AspNetUsers and its claims, logins and
/// tokens). Identity's role tables are left out: roles are <see cref="RoleAssignment"/>s.
/// </summary>
public sealed class AggregatorDbContext(DbContextOptions<AggregatorDbContext> options)
    : IdentityUserContext<AccountUser, Guid>(options), IUnitOfWork
{
    public DbSet<DockerEnvironment> Environments => Set<DockerEnvironment>();

    public DbSet<GitRepository> Repositories => Set<GitRepository>();

    public DbSet<Stack> Stacks => Set<Stack>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<DiscordLink> DiscordLinks => Set<DiscordLink>();

    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();

    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<RegistryCredential> RegistryCredentials => Set<RegistryCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AggregatorDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.AddStronglyTypedId<EnvironmentId>();
        configurationBuilder.AddStronglyTypedId<RepositoryId>();
        configurationBuilder.AddStronglyTypedId<StackId>();
        configurationBuilder.AddStronglyTypedId<DeploymentId>();
        configurationBuilder.AddStronglyTypedId<UserId>();
        configurationBuilder.AddStronglyTypedId<RoleAssignmentId>();
        configurationBuilder.AddStronglyTypedId<NotificationChannelId>();
        configurationBuilder.AddStronglyTypedId<NotificationRuleId>();
        configurationBuilder.AddStronglyTypedId<NotificationId>();
        configurationBuilder.AddStronglyTypedId<RegistryCredentialId>();

        // SQLite has no DateTimeOffset type: stored as text it cannot be compared or ordered in queries.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}
