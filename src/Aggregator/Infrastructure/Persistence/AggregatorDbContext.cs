using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

public sealed class AggregatorDbContext(DbContextOptions<AggregatorDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<DockerEnvironment> Environments => Set<DockerEnvironment>();

    public DbSet<GitRepository> Repositories => Set<GitRepository>();

    public DbSet<Stack> Stacks => Set<Stack>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<DiscordLink> DiscordLinks => Set<DiscordLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AggregatorDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.AddStronglyTypedId<EnvironmentId>();
        configurationBuilder.AddStronglyTypedId<RepositoryId>();
        configurationBuilder.AddStronglyTypedId<StackId>();
        configurationBuilder.AddStronglyTypedId<DeploymentId>();
        configurationBuilder.AddStronglyTypedId<UserId>();
        configurationBuilder.AddStronglyTypedId<RoleAssignmentId>();

        // SQLite has no DateTimeOffset type: stored as text it cannot be compared or ordered in queries.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}
