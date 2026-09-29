using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Docker;

/// <summary>
/// The agent's view of the local Docker Engine. Failures are reported as <see cref="AgentCommandException"/>
/// subclasses, so command handlers do not depend on the Docker client library.
/// </summary>
public interface IDockerGateway
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>All containers, including stopped ones.</summary>
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken);

    /// <summary>Succeeds when the container is already running.</summary>
    Task StartContainerAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Succeeds when the container is already stopped.</summary>
    /// <param name="stopTimeout">How long the process may shut down gracefully before Docker kills it.</param>
    Task StopContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    Task RestartContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);
}
