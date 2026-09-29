using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// An expected failure of a command. The aggregator maps <see cref="Code"/> to the HTTP status of the panel's
/// request and shows <see cref="Exception.Message"/> to the user, so the message must be readable and free of secrets.
/// </summary>
public abstract class AgentCommandException(ErrorCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ErrorCode Code { get; } = code;

    public CommandError ToCommandError() => new() { Code = Code, Message = Message };
}

public sealed class InvalidCommandArgumentException(string message)
    : AgentCommandException(ErrorCode.InvalidArgument, message);

public sealed class ContainerNotFoundException(string containerId, Exception? innerException = null)
    : AgentCommandException(ErrorCode.NotFound, $"Container '{containerId}' was not found.", innerException)
{
    public string ContainerId { get; } = containerId;
}

public sealed class DockerUnavailableException(Exception innerException)
    : AgentCommandException(ErrorCode.DockerUnavailable, "Docker is not reachable on this host.", innerException);

public sealed class DockerCommandFailedException(string message, Exception? innerException = null)
    : AgentCommandException(ErrorCode.CommandFailed, message, innerException);

public sealed class DockerTimeoutException(TimeSpan timeout)
    : AgentCommandException(ErrorCode.Timeout, $"Docker did not answer within {timeout.TotalSeconds:0} seconds.")
{
    public TimeSpan Timeout { get; } = timeout;
}

public sealed class UnsupportedCommandException(string commandName)
    : AgentCommandException(ErrorCode.Unsupported, $"This agent version does not support the {commandName} command.");
