using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

internal class EfRepository<TEntity, TId>(AggregatorDbContext db) : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    protected AggregatorDbContext Db { get; } = db;

    protected DbSet<TEntity> Set => Db.Set<TEntity>();

    public async Task<TEntity?> FindAsync(TId id, CancellationToken cancellationToken) =>
        await Set.FindAsync([id], cancellationToken);

    public async Task<TEntity> GetAsync(TId id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException(typeof(TEntity).Name, id);

    public void Add(TEntity entity) => Set.Add(entity);

    public void Remove(TEntity entity) => Set.Remove(entity);
}

internal sealed class EnvironmentRepository(AggregatorDbContext db)
    : EfRepository<DockerEnvironment, EnvironmentId>(db), IEnvironmentRepository
{
    public Task<DockerEnvironment?> FindByEnrollmentTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(e => e.EnrollmentTokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<DockerEnvironment>> ListAsync(CancellationToken cancellationToken) =>
        await Set.OrderBy(e => e.Name).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        Set.AnyAsync(e => e.Name == name, cancellationToken);
}

internal sealed class StackRepository(AggregatorDbContext db) : EfRepository<Stack, StackId>(db), IStackRepository
{
    public Task<Stack?> FindByNameAsync(EnvironmentId environmentId, StackName name, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(s => s.EnvironmentId == environmentId && s.Name == name, cancellationToken);

    public async Task<IReadOnlyList<Stack>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await Set.Where(s => s.EnvironmentId == environmentId).ToListAsync(cancellationToken);
}

internal sealed class UserRepository(AggregatorDbContext db) : EfRepository<User, UserId>(db), IUserRepository
{
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(u => u.UserName == userName, cancellationToken);
}

internal sealed class DiscordLinkRepository(AggregatorDbContext db) : EfRepository<DiscordLink, UserId>(db), IDiscordLinkRepository
{
    public Task<DiscordLink?> FindByDiscordUserAsync(ulong discordUserId, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(l => l.DiscordUserId == discordUserId, cancellationToken);

    public Task<DiscordLink?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(l => l.PendingCodeHash == codeHash, cancellationToken);
}

internal sealed class RoleAssignmentRepository(AggregatorDbContext db)
    : EfRepository<RoleAssignment, RoleAssignmentId>(db), IRoleAssignmentRepository
{
    public async Task<IReadOnlyList<RoleAssignment>> ListForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        await Set.Where(a => a.UserId == userId).ToListAsync(cancellationToken);
}

internal sealed class GitRepositoryRepository(AggregatorDbContext db)
    : EfRepository<GitRepository, RepositoryId>(db), IGitRepositoryRepository
{
    public async Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken) =>
        await Set.OrderBy(r => r.Name).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        Set.AnyAsync(r => r.Name == name, cancellationToken);
}

internal sealed class DeploymentRepository(AggregatorDbContext db)
    : EfRepository<Deployment, DeploymentId>(db), IDeploymentRepository
{
    private static readonly DeploymentStatus[] Unfinished = [DeploymentStatus.Queued, DeploymentStatus.Running];

    public Task<bool> HasUnfinishedAsync(StackId stackId, CancellationToken cancellationToken) =>
        Set.AnyAsync(d => d.StackId == stackId && Unfinished.Contains(d.Status), cancellationToken);

    public async Task<IReadOnlyList<Deployment>> ListUnfinishedAsync(CancellationToken cancellationToken) =>
        await Set.Where(d => Unfinished.Contains(d.Status)).ToListAsync(cancellationToken);

    // The projection leaves the log out of the query.
    public async Task<IReadOnlyList<DeploymentSummary>> ListByStackAsync(
        StackId stackId, int limit, CancellationToken cancellationToken) =>
        await Set.Where(d => d.StackId == stackId)
            .OrderByDescending(d => d.QueuedAt)
            .Take(limit)
            .Select(d => new DeploymentSummary(
                d.Id, d.Status, d.Trigger, d.PullPolicy, d.Commit, d.QueuedAt, d.StartedAt, d.FinishedAt, d.Error))
            .ToListAsync(cancellationToken);
}

internal sealed class NotificationChannelRepository(AggregatorDbContext db)
    : EfRepository<NotificationChannel, NotificationChannelId>(db), INotificationChannelRepository
{
    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken) =>
        await Set.OrderBy(c => c.Name).ToListAsync(cancellationToken);
}

internal sealed class NotificationRuleRepository(AggregatorDbContext db)
    : EfRepository<NotificationRule, NotificationRuleId>(db), INotificationRuleRepository
{
    public async Task<IReadOnlyList<NotificationRule>> ListActiveAsync(CancellationToken cancellationToken) =>
        await Set.Where(r => r.IsEnabled)
            .Join(Db.NotificationChannels.Where(c => c.IsEnabled), r => r.ChannelId, c => c.Id, (r, _) => r)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<NotificationRule>> ListByChannelAsync(
        NotificationChannelId channelId, CancellationToken cancellationToken) =>
        await Set.Where(r => r.ChannelId == channelId).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken);
}

internal sealed class NotificationRepository(AggregatorDbContext db)
    : EfRepository<Notification, NotificationId>(db), INotificationRepository
{
    public async Task<IReadOnlyList<Notification>> ListPendingAsync(CancellationToken cancellationToken) =>
        await Set.Where(n => n.Status == NotificationStatus.Pending).OrderBy(n => n.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await Set.OrderByDescending(n => n.CreatedAt).Take(limit).ToListAsync(cancellationToken);
}

internal sealed class RegistryCredentialRepository(AggregatorDbContext db)
    : EfRepository<RegistryCredential, RegistryCredentialId>(db), IRegistryCredentialRepository
{
    public async Task<IReadOnlyList<RegistryCredential>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken) =>
        await Set.Where(c => c.EnvironmentId == environmentId).OrderBy(c => c.Server).ToListAsync(cancellationToken);

    public Task<RegistryCredential?> FindByServerAsync(EnvironmentId environmentId, string server, CancellationToken cancellationToken) =>
        Set.SingleOrDefaultAsync(c => c.EnvironmentId == environmentId && c.Server == server, cancellationToken);
}
