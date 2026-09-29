using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface IUserRepository : IRepository<User, UserId>
{
    /// <summary>Case-insensitive.</summary>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);
}

public interface IRoleAssignmentRepository : IRepository<RoleAssignment, RoleAssignmentId>
{
    Task<IReadOnlyList<RoleAssignment>> ListForUserAsync(UserId userId, CancellationToken cancellationToken);
}
