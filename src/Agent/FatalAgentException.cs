namespace ContainerForest.Agent;

/// <summary>A problem that retrying cannot fix; the agent stops and an administrator has to act.</summary>
public sealed class FatalAgentException(string message, Exception? innerException = null)
    : Exception(message, innerException);
