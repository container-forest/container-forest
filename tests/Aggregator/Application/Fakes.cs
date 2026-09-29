using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
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
}

internal sealed class InMemoryRoleAssignments : InMemoryRepository<RoleAssignment, RoleAssignmentId>, IRoleAssignmentRepository
{
    public Task<IReadOnlyList<RoleAssignment>> ListForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RoleAssignment>>(Items.Where(a => a.UserId == userId).ToList());
}

/// <summary>Agent that reports a fixed set of containers and records the actions it was asked to run.</summary>
internal sealed class FakeAgentChannel : IAgentChannel
{
    public List<ContainerSnapshot> Containers { get; } = [];

    public List<string> Calls { get; } = [];

    public TimeSpan? LastStopTimeout { get; private set; }

    public bool IsOnline(EnvironmentId environmentId) => true;

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
}
