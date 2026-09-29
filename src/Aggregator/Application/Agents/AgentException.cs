using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Agents;

/// <summary>A command to an agent could not be completed.</summary>
public abstract class AgentException(EnvironmentId environmentId, string message) : Exception(message)
{
    public EnvironmentId EnvironmentId { get; } = environmentId;
}

/// <summary>The agent is not connected, so the command was not sent. Maps to HTTP 503.</summary>
public sealed class AgentOfflineException(EnvironmentId environmentId)
    : AgentException(environmentId, $"Agent of environment {environmentId} is offline.");

/// <summary>The agent disconnected after the command was sent and before it answered. Maps to HTTP 503.</summary>
public sealed class AgentDisconnectedException(EnvironmentId environmentId)
    : AgentException(environmentId, $"Agent of environment {environmentId} disconnected before answering.");

/// <summary>The agent did not answer in time. Maps to HTTP 504.</summary>
public sealed class AgentTimeoutException(EnvironmentId environmentId, TimeSpan timeout)
    : AgentException(environmentId, $"Agent of environment {environmentId} did not answer within {timeout.TotalSeconds:0} s.")
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>The agent ran the command and reported an error.</summary>
public sealed class AgentCommandFailedException(EnvironmentId environmentId, AgentErrorKind kind, string message)
    : AgentException(environmentId, message)
{
    public AgentErrorKind Kind { get; } = kind;
}

public enum AgentErrorKind
{
    Unknown,
    InvalidArgument,
    NotFound,
    DockerUnavailable,
    CommandFailed,
    Timeout,
    Cancelled,
    Busy,
    Internal,

    /// <summary>The agent version has no handler for the command; updating the agent fixes it.</summary>
    Unsupported,
}
