using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Containers;

/// <summary>Finds a container by the reference a user gives, the way the docker CLI does.</summary>
public static class ContainerLookup
{
    /// <summary>
    /// Tries, in order: the full id, the exact name, then an id prefix such as the 12-character short id.
    /// </summary>
    /// <exception cref="NotFoundException">Nothing matches.</exception>
    /// <exception cref="DomainConflictException">The prefix matches more than one container.</exception>
    public static ContainerSnapshot Find(IReadOnlyList<ContainerSnapshot> containers, string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw new NotFoundException("Container", reference);
        }

        var byIdOrName = containers.FirstOrDefault(c => c.Id == reference)
            ?? containers.FirstOrDefault(c => c.Name == reference);
        if (byIdOrName is not null)
        {
            return byIdOrName;
        }

        var byPrefix = containers.Where(c => c.Id.StartsWith(reference, StringComparison.Ordinal)).Take(2).ToList();
        return byPrefix.Count switch
        {
            1 => byPrefix[0],
            0 => throw new NotFoundException("Container", reference),
            _ => throw new DomainConflictException(
                $"Container id prefix '{reference}' matches more than one container; give more characters."),
        };
    }
}
