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

    /// <summary>
    /// True when the file has CRLF line endings. sops never writes CR, so in a sops file it means a tool converted
    /// it, usually git on Windows (core.autocrlf), and sops cannot read the metadata of a dotenv or INI file any more.
    /// </summary>
    public static bool HasCrLf(byte[] content) => content.AsSpan().IndexOf("\r\n"u8) >= 0;

    /// <summary>True when the two differ at most in CR before LF: one is the other with converted line endings.</summary>
    public static bool SameButLineEndings(byte[] a, byte[] b) => WithoutCr(a).AsSpan().SequenceEqual(WithoutCr(b));

    /// <summary>
    /// Plain text as sops should get it: a dotenv or INI file in LF. sops keeps a CR before LF as part of the value, so
    /// a file saved with CRLF (an editor on Windows) would deploy <c>TOKEN=x\r</c>; YAML and JSON treat CR as a line
    /// break or space themselves, and anything else may be binary.
    /// </summary>
    public static byte[] PlainForSops(string path, byte[] content)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".env", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            ? WithoutCr(content)
            : content;
    }

    /// <summary>The content with CRLF line endings turned into LF; a lone CR stays.</summary>
    public static byte[] WithoutCr(byte[] content) =>
        HasCrLf(content) ? content.Where((b, i) => b != '\r' || i + 1 >= content.Length || content[i + 1] != '\n').ToArray() : content;

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
