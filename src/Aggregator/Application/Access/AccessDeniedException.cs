namespace ContainerForest.Aggregator.Application.Access;

/// <summary>The user is known but lacks the permission for the operation. Maps to HTTP 403.</summary>
public sealed class AccessDeniedException(string message) : Exception(message);
