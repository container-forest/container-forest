using System.Collections.Frozen;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>Routes each aggregator command to the handler registered for its type.</summary>
public sealed class CommandDispatcher
{
    private readonly FrozenDictionary<ControllerMessage.BodyOneofCase, IAgentCommandHandler> handlers;
    private readonly ILogger<CommandDispatcher> logger;

    /// <exception cref="ArgumentException">Two handlers claim the same command type.</exception>
    public CommandDispatcher(IEnumerable<IAgentCommandHandler> handlers, ILogger<CommandDispatcher> logger)
    {
        this.handlers = handlers.ToFrozenDictionary(h => h.Handles);
        this.logger = logger;
    }

    public IReadOnlyCollection<ControllerMessage.BodyOneofCase> SupportedCommands => handlers.Keys;

    /// <summary>
    /// Runs the command and returns its result, or null for messages that get no result (CancelRequest).
    /// Every other message gets exactly one result, so the aggregator never waits for its timeout.
    /// </summary>
    public async Task<CommandResult?> DispatchAsync(
        ControllerMessage message, ICommandProgress progress, CancellationToken cancellationToken)
    {
        switch (message.BodyCase)
        {
            case ControllerMessage.BodyOneofCase.CancelRequest:
                // Cancellation arrives with long-running commands (deploy, log streaming). Unknown ids are ignored by contract.
                logger.LogDebug("Ignoring cancellation of request {RequestId}", message.CancelRequest.TargetRequestId);
                return null;

            case ControllerMessage.BodyOneofCase.None:
                // A command added to the protocol after this agent was built arrives as an unknown field.
                return Unsupported(message, "unknown");

            default:
                return handlers.TryGetValue(message.BodyCase, out var handler)
                    ? await handler.HandleAsync(message, progress, cancellationToken)
                    : Unsupported(message, message.BodyCase.ToString());
        }
    }

    private CommandResult Unsupported(ControllerMessage message, string commandName)
    {
        logger.LogWarning("Request {RequestId}: command {Command} is not supported by this agent", message.RequestId, commandName);
        return new CommandResult { RequestId = message.RequestId, Error = new UnsupportedCommandException(commandName).ToCommandError() };
    }
}
