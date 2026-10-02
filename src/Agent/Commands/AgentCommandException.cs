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

public sealed class UnsupportedOptionException(string option)
    : AgentCommandException(ErrorCode.Unsupported, $"This agent version does not support {option}.");

public sealed class StackNotFoundException(string stackName)
    : AgentCommandException(ErrorCode.NotFound, $"Stack '{stackName}' is not deployed on this host.")
{
    public string StackName { get; } = stackName;
}

public sealed class StackBusyException(string stackName)
    : AgentCommandException(ErrorCode.Busy, $"Another operation on stack '{stackName}' is running; try again when it has finished.")
{
    public string StackName { get; } = stackName;
}

public sealed class ComposeUnavailableException(string executable, Exception innerException)
    : AgentCommandException(
        ErrorCode.DockerUnavailable, $"The docker CLI ('{executable}') could not be started on this host.", innerException);

public sealed class ComposeFailedException(string operation, int exitCode)
    : AgentCommandException(ErrorCode.CommandFailed, $"{operation} exited with code {exitCode}; see the command output.")
{
    public int ExitCode { get; } = exitCode;
}

public sealed class ComposeTimeoutException(string operation, TimeSpan timeout)
    : AgentCommandException(ErrorCode.Timeout, $"{operation} did not finish within {timeout.TotalMinutes:0.#} minutes and was stopped.")
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>The bundle cannot be written because of what is on the host, e.g. a container created a directory where the bundle has a file.</summary>
public sealed class StackFileConflictException(string path, string reason)
    : AgentCommandException(ErrorCode.CommandFailed, $"Cannot write '{path}' in the stack directory: {reason}")
{
    public string Path { get; } = path;
}
