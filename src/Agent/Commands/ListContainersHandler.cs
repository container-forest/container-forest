using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

public sealed class ListContainersHandler(IDockerGateway docker, ILogger<ListContainersHandler> logger)
    : AgentCommandHandler<ListContainers>(logger)
{
    protected override async Task<CommandResult> ExecuteAsync(
        ListContainers command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var containers = await docker.ListContainersAsync(cancellationToken);
        return new CommandResult { Containers = new ContainerList { Items = { containers } } };
    }
}
