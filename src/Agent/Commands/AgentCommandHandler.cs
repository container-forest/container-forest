using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Base for handlers of one command type. Takes <typeparamref name="TCommand"/> out of the message, runs
/// <see cref="ExecuteAsync"/> and turns every outcome into a <see cref="CommandResult"/> with the command's request_id.
/// </summary>
public abstract class AgentCommandHandler<TCommand>(ILogger logger) : IAgentCommandHandler
    where TCommand : class, IMessage<TCommand>
{
    // The oneof field of ControllerMessage that carries TCommand. BodyOneofCase values are the field numbers,
    // so the handled case follows from the type and cannot be set to a mismatching value by mistake.
    private static readonly FieldDescriptor CommandField = ControllerMessage.Descriptor.Fields.InDeclarationOrder()
        .Single(f => f.FieldType == FieldType.Message && f.MessageType.ClrType == typeof(TCommand));

    public ControllerMessage.BodyOneofCase Handles => (ControllerMessage.BodyOneofCase)CommandField.FieldNumber;

    public async Task<CommandResult> HandleAsync(
        ControllerMessage message, ICommandProgress progress, CancellationToken cancellationToken)
    {
        if (message.BodyCase != Handles)
        {
            throw new ArgumentException($"{GetType().Name} handles {Handles}, not {message.BodyCase}.", nameof(message));
        }

        var command = (TCommand)CommandField.Accessor.GetValue(message);
        CommandResult result;
        try
        {
            result = await ExecuteAsync(command, progress, cancellationToken);
        }
        catch (AgentCommandException ex)
        {
            logger.LogWarning("{Command} {RequestId} failed with {Code}: {Reason}", Handles, message.RequestId, ex.Code, ex.Message);
            result = new CommandResult { Error = ex.ToCommandError() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new CommandResult { Error = new CommandError { Code = ErrorCode.Cancelled, Message = "The command was cancelled." } };
        }
        catch (Exception ex)
        {
            // Details stay in the agent log: the message goes to the panel, and exception texts may contain anything.
            logger.LogError(ex, "{Command} {RequestId} failed unexpectedly", Handles, message.RequestId);
            result = new CommandResult
            {
                Error = new CommandError { Code = ErrorCode.Internal, Message = "The agent failed to run the command; see the agent log." },
            };
        }

        result.RequestId = message.RequestId;
        return result;
    }

    /// <summary>Runs the command. Expected failures are thrown as <see cref="AgentCommandException"/>.</summary>
    protected abstract Task<CommandResult> ExecuteAsync(TCommand command, ICommandProgress progress, CancellationToken cancellationToken);
}
