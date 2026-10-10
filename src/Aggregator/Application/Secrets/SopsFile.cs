using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Secrets;

/// <summary>
/// Recognizes files encrypted with SOPS by the metadata sops writes into each format, so that a file that merely
/// mentions "sops" or "ENC[" (documentation, a script) is deployed as it is. The format comes from the extension,
/// the same way sops chooses it.
/// </summary>
public static partial class SopsFile
{
    private static readonly byte[] Marker = "sops"u8.ToArray();

    public static bool IsEncrypted(StackFilePath path, byte[] content)
    {
        // Cheap check first: most files of a stack are not encrypted, and some are large.
        if (content.AsSpan().IndexOf(Marker) < 0)
        {
            return false;
        }

        return FormatOf(path) switch
        {
            SopsFormat.Yaml => YamlMetadata().IsMatch(Text(content)),
            SopsFormat.Dotenv => DotenvMetadata().IsMatch(Text(content)),
            SopsFormat.Ini => IniMetadata().IsMatch(Text(content)),
            SopsFormat.Json => HasJsonMetadata(content, requireData: false),
            _ => HasJsonMetadata(content, requireData: true),
        };
    }

    /// <summary>
    /// The age recipients named in the file's metadata. Only a hint for choosing keys: whoever wrote the file chose what
    /// it says, so the keys are still limited to what the caller may use, and sops decides with the keys themselves.
    /// </summary>
    public static IReadOnlySet<string> AgeRecipients(byte[] content) =>
        AgeRecipient().Matches(Text(content)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static SopsFormat FormatOf(StackFilePath path)
    {
        var name = path.FileName;
        if (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return SopsFormat.Yaml;
        }

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return SopsFormat.Json;
        }

        if (name.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return SopsFormat.Dotenv;
        }

        return name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? SopsFormat.Ini : SopsFormat.Binary;
    }

    // A binary file is stored by sops as {"data": "ENC[…]", "sops": {…}}; JSON and YAML keep "sops" as a top-level key.
    private static bool HasJsonMetadata(byte[] content, bool requireData)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sops", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && (!requireData || (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Text(byte[] content) => Encoding.UTF8.GetString(content);

    [GeneratedRegex(@"^sops:[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex YamlMetadata();

    // "age1" and 58 lowercase Bech32 characters, not part of a longer word.
    [GeneratedRegex(@"(?<![0-9a-z])age1[02-9ac-hj-np-z]{58}(?![0-9a-z])")]
    private static partial Regex AgeRecipient();

    [GeneratedRegex(@"^sops_version=", RegexOptions.Multiline)]
    private static partial Regex DotenvMetadata();

    [GeneratedRegex(@"^\[sops\][ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex IniMetadata();

    private enum SopsFormat
    {
        Yaml,
        Json,
        Dotenv,
        Ini,
        Binary,
    }
}
