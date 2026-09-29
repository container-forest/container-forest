using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Environments;

public interface IEnvironmentRepository : IRepository<DockerEnvironment, EnvironmentId>
{
    Task<DockerEnvironment?> FindByEnrollmentTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<DockerEnvironment>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Case-insensitive.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken);
}
