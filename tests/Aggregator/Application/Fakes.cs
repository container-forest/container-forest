using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests;

internal class InMemoryRepository<TEntity, TId> : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    protected List<TEntity> Items { get; } = [];

    public Task<TEntity?> FindAsync(TId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(e => e.Id.Equals(id)));

    public async Task<TEntity> GetAsync(TId id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException(typeof(TEntity).Name, id);

    public void Add(TEntity entity) => Items.Add(entity);

    public void Remove(TEntity entity) => Items.Remove(entity);
}

internal sealed class InMemoryUsers : InMemoryRepository<User, UserId>, IUserRepository
{
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>(Items.OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Items.Count(u => u.IsAdmin && u.IsActive));
}

/// <summary>Accounts as passwords in memory; records which users had their sessions ended.</summary>
internal sealed class FakeAccountStore : IAccountStore
{
    public const int MinPasswordLength = 12;

    public Dictionary<UserId, string> Passwords { get; } = [];

    public Dictionary<UserId, string> UserNames { get; } = [];

    public Dictionary<UserId, DateTimeOffset> Lockouts { get; } = [];

    public List<UserId> EndedSessions { get; } = [];

    public Task CreateAsync(User user, string password, CancellationToken cancellationToken)
    {
        Validate(password);
        Passwords[user.Id] = password;
        UserNames[user.Id] = user.UserName;
        return Task.CompletedTask;
    }

    public Task RenameAsync(User user, CancellationToken cancellationToken)
    {
        UserNames[user.Id] = user.UserName;
        return Task.CompletedTask;
    }

    public Task SetPasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        Validate(password);
        Passwords[user.Id] = password;
        EndedSessions.Add(user.Id);
        return Task.CompletedTask;
    }

    public Task EndSessionsAsync(UserId userId, CancellationToken cancellationToken)
    {
        EndedSessions.Add(userId);
        return Task.CompletedTask;
    }

    public Task UnlockAsync(UserId userId, CancellationToken cancellationToken)
    {
        Lockouts.Remove(userId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<UserId, DateTimeOffset>> ListLockoutsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<UserId, DateTimeOffset>>(Lockouts.Where(l => l.Value > now).ToDictionary());

    private static void Validate(string password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new PasswordRejectedException([$"Passwords must be at least {MinPasswordLength} characters."]);
        }
    }
}

internal sealed class InMemoryEnvironments : InMemoryRepository<DockerEnvironment, EnvironmentId>, IEnvironmentRepository
{
    public Task<DockerEnvironment?> FindByEnrollmentTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(e => e.EnrollmentTokenHash == tokenHash));

    public Task<IReadOnlyList<DockerEnvironment>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DockerEnvironment>>(Items.ToList());

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)));
}

internal sealed class InMemoryStacks : InMemoryRepository<Stack, StackId>, IStackRepository
{
    public Task<Stack?> FindByNameAsync(EnvironmentId environmentId, StackName name, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(s => s.EnvironmentId == environmentId && s.Name == name));

    public Task<IReadOnlyList<Stack>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Stack>>(Items.Where(s => s.EnvironmentId == environmentId).ToList());
}

internal sealed class InMemoryDiscordLinks : InMemoryRepository<DiscordLink, UserId>, IDiscordLinkRepository
{
    public Task<DiscordLink?> FindByDiscordUserAsync(ulong discordUserId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(l => l.DiscordUserId == discordUserId));

    public Task<DiscordLink?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(l => l.PendingCodeHash == codeHash));
}

internal sealed class InMemoryRoleAssignments : InMemoryRepository<RoleAssignment, RoleAssignmentId>, IRoleAssignmentRepository
{
    public Task<IReadOnlyList<RoleAssignment>> ListForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RoleAssignment>>(Items.Where(a => a.UserId == userId).ToList());
}

internal sealed class InMemoryGitRepositories : InMemoryRepository<GitRepository, RepositoryId>, IGitRepositoryRepository
{
    public Task<IReadOnlyList<GitRepository>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitRepository>>(Items.OrderBy(r => r.Name).ToList());

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
}

internal sealed class InMemoryDeployments : InMemoryRepository<Deployment, DeploymentId>, IDeploymentRepository
{
    public IReadOnlyList<Deployment> All => Items;

    public Task<bool> HasUnfinishedAsync(StackId stackId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Any(d => d.StackId == stackId && !d.IsFinished));

    public Task<IReadOnlyList<Deployment>> ListUnfinishedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Deployment>>(Items.Where(d => !d.IsFinished).ToList());

    public Task<IReadOnlyList<DeploymentSummary>> ListByStackAsync(StackId stackId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentSummary>>(Items
            .Where(d => d.StackId == stackId)
            .OrderByDescending(d => d.QueuedAt)
            .Take(limit)
            .Select(d => new DeploymentSummary(
                d.Id, d.Status, d.Trigger, d.PullPolicy, d.Commit, d.QueuedAt, d.StartedAt, d.FinishedAt, d.Error))
            .ToList());
}

internal sealed class FakeDeploymentQueue : IDeploymentQueue
{
    public List<DeploymentId> Queued { get; } = [];

    public void Enqueue(DeploymentId id) => Queued.Add(id);
}

internal sealed class FakeCurrentUser : ICurrentUser
{
    public UserId? Id { get; set; }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

/// <summary>Agent that reports a fixed set of containers and records the actions it was asked to run.</summary>
internal sealed class FakeAgentChannel : IAgentChannel
{
    public List<ContainerSnapshot> Containers { get; } = [];

    public List<string> Calls { get; } = [];

    public TimeSpan? LastStopTimeout { get; private set; }

    public bool Online { get; set; } = true;

    public StackBundle? LastBundle { get; private set; }

    /// <summary>Compose output the agent reports while deploying, restarting or taking down a stack.</summary>
    public List<string> DeployOutput { get; } = [];

    /// <summary>Thrown by a compose command (deploy, restart, down) after the output is reported.</summary>
    public Exception? DeployError { get; set; }

    public bool IsOnline(EnvironmentId environmentId) => Online;

    public Task<IReadOnlyList<ContainerSnapshot>> ListContainersAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        Calls.Add("list");
        return Task.FromResult<IReadOnlyList<ContainerSnapshot>>(Containers.ToList());
    }

    public Task StartContainerAsync(EnvironmentId environmentId, string containerId, CancellationToken cancellationToken)
    {
        Calls.Add($"start {containerId}");
        return Task.CompletedTask;
    }

    public Task StopContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        Calls.Add($"stop {containerId}");
        LastStopTimeout = stopTimeout;
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        Calls.Add($"restart {containerId}");
        LastStopTimeout = stopTimeout;
        return Task.CompletedTask;
    }

    public Task DeployStackAsync(
        EnvironmentId environmentId, StackBundle bundle, Action<string> onProgress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            throw new AgentOfflineException(environmentId);
        }

        Calls.Add($"deploy {bundle.StackName}");
        LastBundle = bundle;
        DeployOutput.ForEach(onProgress);
        return DeployError is null ? Task.CompletedTask : Task.FromException(DeployError);
    }

    public Task RestartStackAsync(
        EnvironmentId environmentId, StackName stackName, TimeSpan stopTimeout, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunStackCommand($"restart-stack {stackName}", environmentId, stopTimeout, onProgress, cancellationToken);

    public Task DownStackAsync(
        EnvironmentId environmentId, StackName stackName, TimeSpan stopTimeout, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunStackCommand($"down-stack {stackName}", environmentId, stopTimeout, onProgress, cancellationToken);

    /// <summary>Lines the agent returns for any container's log.</summary>
    public List<ContainerLogLine> LogLines { get; } = [];

    public ContainerLogQuery? LastLogQuery { get; private set; }

    public Task<IReadOnlyList<ContainerLogLine>> ReadContainerLogsAsync(
        EnvironmentId environmentId, string containerId, ContainerLogQuery query, CancellationToken cancellationToken)
    {
        Calls.Add($"logs {containerId}");
        LastLogQuery = query;
        return Task.FromResult<IReadOnlyList<ContainerLogLine>>(LogLines.ToList());
    }

    /// <summary>Whether the last stack command got a token that can be cancelled.</summary>
    public bool? LastStackTokenCancellable { get; private set; }

    private Task RunStackCommand(
        string call, EnvironmentId environmentId, TimeSpan stopTimeout, Action<string> onProgress, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            throw new AgentOfflineException(environmentId);
        }

        Calls.Add(call);
        LastStopTimeout = stopTimeout;
        LastStackTokenCancellable = cancellationToken.CanBeCanceled;
        DeployOutput.ForEach(onProgress);
        return DeployError is null ? Task.CompletedTask : Task.FromException(DeployError);
    }
}

internal sealed class InMemoryRegistryCredentials : InMemoryRepository<RegistryCredential, RegistryCredentialId>, IRegistryCredentialRepository
{
    public IReadOnlyList<RegistryCredential> All => Items;

    public Task<IReadOnlyList<RegistryCredential>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RegistryCredential>>(Items.Where(c => c.EnvironmentId == environmentId).OrderBy(c => c.Server).ToList());

    public Task<RegistryCredential?> FindByServerAsync(EnvironmentId environmentId, string server, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(c => c.EnvironmentId == environmentId && c.Server == server));
}

/// <summary>"Encrypts" by adding a purpose prefix, so tests can see that a value was protected and for what.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(SecretPurpose purpose, string plaintext) => $"{purpose}:{plaintext}";

    public string Unprotect(SecretPurpose purpose, string protectedValue) =>
        protectedValue.StartsWith($"{purpose}:", StringComparison.Ordinal)
            ? protectedValue[(purpose.ToString().Length + 1)..]
            : throw new SecretUnreadableException(purpose, new InvalidOperationException("Not protected by this fake."));
}
