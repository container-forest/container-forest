namespace ContainerForest.Aggregator.Domain.Common;

/// <summary>A domain rule was violated.</summary>
public abstract class DomainException(string message) : Exception(message);

/// <summary>The input breaks a domain rule, e.g. an invalid stack name. Maps to HTTP 400.</summary>
public sealed class DomainValidationException(string message) : DomainException(message);

/// <summary>The operation is not allowed in the current state, e.g. finishing a queued deployment. Maps to HTTP 409.</summary>
public sealed class DomainConflictException(string message) : DomainException(message);
