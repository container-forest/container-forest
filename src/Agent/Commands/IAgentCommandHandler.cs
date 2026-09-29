using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>Executes one kind of aggregator command. <see cref="CommandDispatcher"/> picks the handler by <see cref="Handles"/>.</summary>
public interface IAgentCommandHandler
{
    ControllerMessage.BodyOneofCase Handles { get; }

    /// <summary>Never throws for a failed command: the failure is returned as <see cref="CommandResult.Error"/>.</summary>
    Task<CommandResult> HandleAsync(ControllerMessage message, ICommandProgress progress, CancellationToken cancellationToken);
}
