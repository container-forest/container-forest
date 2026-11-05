using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Policies;

namespace ContainerForest.Aggregator.Application.Policies;

public interface IComposePolicyRepository
{
    /// <summary>The environment's policy; <see cref="ComposePolicy.Default"/> (off) when none is stored.</summary>
    Task<ComposePolicy> GetAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>The stored policy, for changing it; null when the environment has the default.</summary>
    Task<ComposePolicy?> FindAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    void Add(ComposePolicy policy);
}

/// <summary>What the agent's render of a stack gave (RenderedStack in agent.proto).</summary>
/// <param name="ModelJson">Compose's normalized model, already without secret-bearing values.</param>
public sealed record RenderedCompose(
    string ModelJson, string StackDirectory, IReadOnlyList<string> AgentDirectories, IReadOnlyList<InspectedMount> InspectedMounts);

/// <summary>
/// Turns compose's normalized model (`docker compose config --format json`, from the agent) into
/// <see cref="ComposeFacts"/>. Shapes the policy does not expect fail closed: a key that is not understood becomes
/// an unknown key, which needs an allowance.
/// </summary>
public static class ComposeFactsReader
{
    private static readonly string[] NamespaceKeys = ["network_mode", "pid", "ipc", "uts", "userns_mode", "cgroup"];

    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    /// <exception cref="JsonException">The model is not a JSON object with services.</exception>
    public static ComposeFacts Read(RenderedCompose rendered)
    {
        var root = JsonNode.Parse(rendered.ModelJson) as JsonObject ?? throw new JsonException("The compose model is not an object.");
        var services = (root["services"] as JsonObject ?? throw new JsonException("The compose model has no services."))
            .Select(p => ReadService(p.Key, p.Value as JsonObject ?? []))
            .ToList();
        var files = new[] { "secrets", "configs" }
            .SelectMany(section => (root[section] as JsonObject ?? []).Select(p => Text(p.Value?["file"])))
            .OfType<string>()
            .ToList();

        return new ComposeFacts(
            rendered.StackDirectory,
            rendered.AgentDirectories,
            services,
            Resources(root["volumes"]),
            Resources(root["networks"]),
            files,
            rendered.InspectedMounts);
    }

    public static string Serialize(ComposeFacts facts) => JsonSerializer.Serialize(facts, StorageOptions);

    /// <summary>Facts kept on a stack; null when they cannot be read (written by another version).</summary>
    public static ComposeFacts? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ComposeFacts>(json, StorageOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ServiceFacts ReadService(string name, JsonObject service)
    {
        var namespaces = NamespaceKeys
            .Select(key => (key, value: Text(service[key])))
            .Where(p => p.value is { Length: > 0 })
            .ToDictionary(p => p.key, p => p.value!, StringComparer.Ordinal);
        var hooksPrivileged = new[] { "post_start", "pre_stop" }
            .SelectMany(hook => (service[hook] as JsonArray ?? []).OfType<JsonObject>())
            .Any(hook => Bool(hook["privileged"]));
        var reservations = service["deploy"]?["resources"]?["reservations"]?["devices"] as JsonArray;

        return new ServiceFacts(
            name,
            Bool(service["privileged"]) || hooksPrivileged,
            namespaces,
            Strings(service["cap_add"]),
            Strings(service["security_opt"]),
            Devices(service["devices"]),
            service["gpus"] is not null || reservations is { Count: > 0 },
            service["device_cgroup_rules"] is JsonArray { Count: > 0 },
            Text(service["cgroup_parent"]) is { Length: > 0 },
            Text(service["runtime"]) is { Length: > 0 } runtime ? runtime : null,
            service["provider"] is not null,
            Bool(service["use_api_socket"]),
            Mounts(service["volumes"]),
            Ports(service["ports"]),
            Strings(service["volumes_from"]),
            Labels(service["labels"]),
            Text(service["container_name"]) is { Length: > 0 } containerName ? containerName : null,
            service.Select(p => p.Key).Where(k => !ComposePolicyEvaluator.KnownServiceKeys.Contains(k)).ToList());
    }

    private static List<MountFacts> Mounts(JsonNode? node)
    {
        var mounts = new List<MountFacts>();
        foreach (var entry in node as JsonArray ?? [])
        {
            if (entry is not JsonObject volume)
            {
                // The normalized model has long syntax only; anything else is not understood, so it counts as a bind of '?'.
                mounts.Add(new MountFacts("bind", Text(entry) ?? "?", ReadOnly: false, null));
                continue;
            }

            mounts.Add(new MountFacts(
                Text(volume["type"]) ?? "?",
                Text(volume["source"]),
                Bool(volume["read_only"]),
                Text(volume["bind"]?["propagation"])));
        }

        return mounts;
    }

    private static List<int> Ports(JsonNode? node)
    {
        var ports = new List<int>();
        foreach (var entry in node as JsonArray ?? [])
        {
            var published = entry is JsonObject port ? Text(port["published"]) : Text(entry);
            if (published is not { Length: > 0 })
            {
                continue;
            }

            var range = published.Split('-', 2);
            if (int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from))
            {
                var to = range.Length == 2 && int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : from;
                // Only the privileged part of a range matters; listing all of a large range is pointless.
                for (var p = from; p <= Math.Min(to, 1023); p++)
                {
                    ports.Add(p);
                }

                if (from > 1023)
                {
                    ports.Add(from);
                }
            }
            else
            {
                // Not a number: count it as port 1, which needs an allowance, rather than let it through.
                ports.Add(1);
            }
        }

        return ports;
    }

    private static List<string> Devices(JsonNode? node) =>
        (node as JsonArray ?? [])
            .Select(entry => entry is JsonObject device ? Text(device["source"]) : Text(entry)?.Split(':')[0])
            .Select(path => path ?? "?")
            .ToList();

    private static List<string> Labels(JsonNode? node) => node switch
    {
        JsonObject map => map.Select(p => p.Key).ToList(),
        JsonArray list => list.Select(Text).OfType<string>().Select(l => l.Split('=', 2)[0]).ToList(),
        _ => [],
    };

    private static List<NamedResource> Resources(JsonNode? node) =>
        (node as JsonObject ?? [])
            .Select(p =>
            {
                var resource = p.Value as JsonObject ?? [];
                return new NamedResource(
                    p.Key,
                    Text(resource["name"]) ?? p.Key,
                    Bool(resource["external"]),
                    Text(resource["driver"]) is { Length: > 0 } driver ? driver : null,
                    resource["driver_opts"] is JsonObject { Count: > 0 });
            })
            .ToList();

    private static List<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(Text).OfType<string>().ToList();

    private static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString()
        : null;

    private static bool Bool(JsonNode? node) => node is JsonValue value
        && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<string>(out var text) && text == "true");
}
