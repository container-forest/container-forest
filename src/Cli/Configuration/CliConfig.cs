using System.Text.Json.Serialization;

namespace ContainerForest.Cli.Configuration;

/// <summary>
/// The CLI's configuration file: named contexts, one per aggregator the user works with, and which one is current.
/// Holds API tokens, so the file is readable by its owner only (<see cref="ConfigStore"/>).
/// </summary>
public sealed class CliConfig
{
    public string? CurrentContext { get; set; }

    public Dictionary<string, ContextEntry> Contexts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The current context, or null when there is none or it no longer exists.</summary>
    [JsonIgnore]
    public ContextEntry? Current => CurrentContext is { } name && Contexts.TryGetValue(name, out var entry) ? entry : null;
}

/// <summary>One aggregator: its address, the token for it and the environment commands use by default.</summary>
public sealed class ContextEntry
{
    public required string Url { get; set; }

    public string? Token { get; set; }

    /// <summary>Whose token it is, as the aggregator said at login; shown only.</summary>
    public string? UserName { get; set; }

    /// <summary>Environment for commands without <c>--env</c>; null when there is no default.</summary>
    public string? Environment { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
internal sealed partial class CliConfigJsonContext : JsonSerializerContext;
