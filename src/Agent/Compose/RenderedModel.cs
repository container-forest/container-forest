using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContainerForest.Agent.Compose;

/// <summary>
/// Compose's normalized model of a stack (`docker compose config --format json`) made fit to send to the aggregator for
/// its compose policy (ADR-0012): paths of the throwaway render directory become the stack directory's, and values
/// that may hold secrets and that the policy does not need are dropped.
/// </summary>
public sealed class RenderedModel
{
    // Keys of a service whose values may carry interpolated secrets; the policy looks at none of them.
    private static readonly string[] SecretBearingServiceKeys = ["environment", "command", "entrypoint", "healthcheck"];

    private readonly JsonObject root;

    private RenderedModel(JsonObject root) => this.root = root;

    /// <exception cref="JsonException">The output is not a JSON object.</exception>
    public static RenderedModel Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new JsonException("The compose model is not a JSON object."));

    /// <summary>Turns every path under <paramref name="from"/> into the same path under <paramref name="to"/>.</summary>
    public RenderedModel MoveDirectory(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(from);
        to = Path.TrimEndingDirectorySeparator(to);
        Rewrite(root, value =>
            value == from ? to
            : value.StartsWith(from + "/", StringComparison.Ordinal) ? to + value[from.Length..]
            : value);
        return this;
    }

    /// <summary>Drops environment values, commands, health checks, label values, inline secret content and x-* extensions.</summary>
    public RenderedModel WithoutSecrets()
    {
        RemoveExtensions(root);
        if (root["services"] is JsonObject services)
        {
            foreach (var (_, node) in services)
            {
                if (node is not JsonObject service)
                {
                    continue;
                }

                foreach (var key in SecretBearingServiceKeys)
                {
                    service.Remove(key);
                }

                ClearValues(service["labels"]);
                ClearValues(service["annotations"]);
                foreach (var hook in new[] { "post_start", "pre_stop" })
                {
                    if (service[hook] is JsonArray hooks)
                    {
                        foreach (var entry in hooks.OfType<JsonObject>())
                        {
                            entry.Remove("command");
                            entry.Remove("environment");
                        }
                    }
                }
            }
        }

        foreach (var section in new[] { "secrets", "configs" })
        {
            if (root[section] is JsonObject items)
            {
                foreach (var (_, item) in items)
                {
                    (item as JsonObject)?.Remove("content");
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Host paths compose would have Docker mount: bind-mount sources and the files of secrets and configs, which
    /// compose also bind-mounts outside Swarm.
    /// </summary>
    public IReadOnlyList<string> MountSources()
    {
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        if (root["services"] is JsonObject services)
        {
            foreach (var volume in services.Select(s => s.Value?["volumes"]).OfType<JsonArray>().SelectMany(v => v).OfType<JsonObject>())
            {
                if (volume["type"]?.GetValue<string>() == "bind" && volume["source"]?.GetValue<string>() is { Length: > 0 } source)
                {
                    sources.Add(source);
                }
            }
        }

        foreach (var section in new[] { "secrets", "configs" })
        {
            if (root[section] is JsonObject items)
            {
                foreach (var file in items.Select(i => i.Value?["file"]).OfType<JsonValue>())
                {
                    if (file.TryGetValue<string>(out var path) && path.Length > 0)
                    {
                        sources.Add(path);
                    }
                }
            }
        }

        return [.. sources];
    }

    public string ToJson() => root.ToJsonString();

    private static void RemoveExtensions(JsonObject node)
    {
        foreach (var key in node.Select(p => p.Key).Where(k => k.StartsWith("x-", StringComparison.Ordinal)).ToList())
        {
            node.Remove(key);
        }

        foreach (var child in node.Select(p => p.Value).OfType<JsonObject>())
        {
            RemoveExtensions(child);
        }
    }

    private static void ClearValues(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var key in map.Select(p => p.Key).ToList())
                {
                    map[key] = "";
                }

                break;
            case JsonArray list:
                // "key=value" entries; only the keys matter.
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsonValue value && value.TryGetValue<string>(out var entry))
                    {
                        var equals = entry.IndexOf('=', StringComparison.Ordinal);
                        list[i] = equals < 0 ? entry : entry[..equals];
                    }
                }

                break;
        }
    }

    private static void Rewrite(JsonNode? node, Func<string, string> rewrite)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var key in map.Select(p => p.Key).ToList())
                {
                    if (map[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        map[key] = rewrite(text);
                    }
                    else
                    {
                        Rewrite(map[key], rewrite);
                    }
                }

                break;
            case JsonArray list:
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        list[i] = rewrite(text);
                    }
                    else
                    {
                        Rewrite(list[i], rewrite);
                    }
                }

                break;
        }
    }
}
