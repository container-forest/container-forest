namespace ContainerForest.Aggregator.Application.Common;

/// <summary>The requested entity does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException(string entityName, object id)
    : Exception($"{entityName} {id} was not found.")
{
    public string EntityName { get; } = entityName;

    public object Id { get; } = id;
}
