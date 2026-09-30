using System.Collections.Frozen;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>Routes each aggregator command to the handler registered for its type and cancels commands on request.</summary>
public sealed class CommandDispatcher
{
    private readonly FrozenDictionary<ControllerMessage.BodyOneofCase, IAgentCommandHandler> handlers;
    private readonly ILogger<CommandDispatcher> logger;
    private readonly RunningCommands running = new();

    /// <exception cref="ArgumentException">Two handlers claim the same command type.</exception>
    public CommandDispatcher(IEnumerable<IAgentCommandHandler> handlers, ILogger<CommandDispatcher> logger)
    {
        this.handlers = handlers.ToFrozenDictionary(h => h.Handles);
        this.logger = logger;
    }

    public IReadOnlyCollection<ControllerMessage.BodyOneofCase> SupportedCommands => handlers.Keys;

    public int RunningCount => running.Count;

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
                // The target has already been registered: the session dispatches each message before it reads the next,
                // and a command registers before its first await. Unknown ids are ignored by contract.
                var target = message.CancelRequest.TargetRequestId;
                if (running.Cancel(target))
                {
                    logger.LogInformation("Cancelling request {RequestId} at the aggregator's request", target);
                }
                else
                {
                    logger.LogDebug("Ignoring cancellation of request {RequestId}: it is not running", target);
                }

                return null;

            case ControllerMessage.BodyOneofCase.None:
                // A command added to the protocol after this agent was built arrives as an unknown field.
                return Unsupported(message, "unknown");

            default:
                if (!handlers.TryGetValue(message.BodyCase, out var handler))
                {
                    return Unsupported(message, message.BodyCase.ToString());
                }

                using (var command = running.Begin(message.RequestId, cancellationToken))
                {
                    if (command is null)
                    {
                        // A second command with the same id could not be told apart from the first when cancelling.
                        logger.LogWarning("Request {RequestId} is already running; rejecting the duplicate", message.RequestId);
                        return new CommandResult
                        {
                            RequestId = message.RequestId,
                            Error = new InvalidCommandArgumentException($"A command with request id '{message.RequestId}' is already running.").ToCommandError(),
                        };
                    }

                    return await handler.HandleAsync(message, progress, command.Token);
                }
        }
    }

    private CommandResult Unsupported(ControllerMessage message, string commandName)
    {
        logger.LogWarning("Request {RequestId}: command {Command} is not supported by this agent", message.RequestId, commandName);
        return new CommandResult { RequestId = message.RequestId, Error = new UnsupportedCommandException(commandName).ToCommandError() };
    }
}
