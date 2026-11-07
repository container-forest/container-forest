using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface IUserRepository : IRepository<User, UserId>
{
    /// <summary>Case-insensitive.</summary>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);

    /// <summary>All users, by user name.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken);

    Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken);
}

public interface IRoleAssignmentRepository : IRepository<RoleAssignment, RoleAssignmentId>
{
    Task<IReadOnlyList<RoleAssignment>> ListForUserAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Roles on the environment and on each of its stacks.</summary>
    Task<IReadOnlyList<RoleAssignment>> ListForEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken);
}
