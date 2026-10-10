using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// Recognizes SOPS files by the metadata sops writes into each format, the same way the aggregator does
/// (Aggregator.Application.Secrets.SopsFile): a file that merely mentions "sops" is plain text. The format comes from
/// the extension, as for sops itself.
/// </summary>
internal static partial class SopsFiles
{
    private static readonly byte[] Marker = "sops"u8.ToArray();

    public static bool IsEncrypted(string path, byte[] content)
    {
        if (content.AsSpan().IndexOf(Marker) < 0)
        {
            return false;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return YamlMetadata().IsMatch(Text(content));
        }

        if (name.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return DotenvMetadata().IsMatch(Text(content));
        }

        if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            return IniMetadata().IsMatch(Text(content));
        }

        return HasJsonMetadata(content, requireData: !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The age recipients named in the file; what the file says, so only good for showing.</summary>
    public static IReadOnlySet<string> AgeRecipients(byte[] content) =>
        AgeRecipient().Matches(Text(content)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

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

    [GeneratedRegex(@"^sops_version=", RegexOptions.Multiline)]
    private static partial Regex DotenvMetadata();

    [GeneratedRegex(@"^\[sops\][ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex IniMetadata();

    [GeneratedRegex(@"(?<![0-9a-z])age1[02-9ac-hj-np-z]{58}(?![0-9a-z])")]
    private static partial Regex AgeRecipient();
}
