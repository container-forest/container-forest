using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace ContainerForest.Aggregator.Infrastructure.Security;

/// <summary>
/// Keeps Data Protection keys in memory only. For a host that must not write a key ring: the build-time OpenAPI
/// generation starts the app, and the default repository would create ~/.aspnet/DataProtection-Keys on every build.
/// </summary>
internal sealed class InMemoryXmlRepository : IXmlRepository
{
    private readonly List<XElement> elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (elements)
        {
            return elements.Select(element => new XElement(element)).ToList();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (elements)
        {
            elements.Add(new XElement(element));
        }
    }
}
