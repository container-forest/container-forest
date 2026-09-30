using ContainerForest.Aggregator.Application.Deployments;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>
/// Reads the services of a compose file through the YAML node tree, not a typed model: only a few keys
/// are checked, and compose allows many shapes (short and long syntax) for the rest.
/// </summary>
internal sealed class YamlComposeParser : IComposeParser
{
    private const string MergeKey = "<<";

    public ComposeFile Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            // The message itself has no position; Start has it.
            throw new DeploymentFailedException(
                $"Compose file is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new DeploymentFailedException("Compose file must be a YAML mapping with a 'services' section.");
        }

        if (Find(root, "services") is not YamlMappingNode services)
        {
            throw new DeploymentFailedException("Compose file has no 'services' section.");
        }

        var result = new List<ComposeService>();
        foreach (var (key, value) in services.Children)
        {
            var name = (key as YamlScalarNode)?.Value ?? key.ToString();
            if (value is not YamlMappingNode service)
            {
                throw new DeploymentFailedException($"Service '{name}' must be a mapping.");
            }

            result.Add(new ComposeService(
                name,
                (Find(service, "image") as YamlScalarNode)?.Value,
                HasBuild: Find(service, "build") is not null,
                Extends: Find(service, "extends") is not null));
        }

        return new ComposeFile(result);
    }

    /// <summary>Looks the key up in the mapping, then in its merge keys ("&lt;&lt;: *defaults"), as compose does.</summary>
    private static YamlNode? Find(YamlMappingNode mapping, string key)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var value))
        {
            return value;
        }

        if (!mapping.Children.TryGetValue(new YamlScalarNode(MergeKey), out var merged))
        {
            return null;
        }

        // YamlStream has already resolved the aliases, so merged mappings are ordinary nodes here.
        var sources = merged switch
        {
            YamlMappingNode single => [single],
            YamlSequenceNode many => many.Children.OfType<YamlMappingNode>().ToList(),
            _ => new List<YamlMappingNode>(),
        };

        return sources.Select(source => Find(source, key)).FirstOrDefault(found => found is not null);
    }
}
