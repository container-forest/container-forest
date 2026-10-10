using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <summary>
/// Picks the creation rule of a <c>.sops.yaml</c> for a path the way sops does: the first rule whose
/// <c>path_regex</c> matches the path relative to the repository root (where the CLI reads <c>.sops.yaml</c>),
/// or the first rule without one.
/// </summary>
/// <remarks>
/// sops uses Go's RE2 syntax. .NET's non-backtracking engine accepts the same subset (no backreferences or
/// lookarounds) and runs in linear time, so a hostile pattern cannot stall the aggregator.
/// </remarks>
internal sealed partial class YamlSopsConfigReader : ISopsConfigReader
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> ValueKeys =
    [
        "encrypted_regex", "unencrypted_regex", "encrypted_suffix", "unencrypted_suffix",
        "encrypted_comment_regex", "unencrypted_comment_regex", "mac_only_encrypted",
    ];

    public SopsEncryptionRule RuleFor(string config, StackFilePath path)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(config));
        }
        catch (YamlException ex)
        {
            throw new SopsConfigException($".sops.yaml is not valid YAML (line {ex.Start.Line}): {ex.Message}");
        }

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || Find(root, "creation_rules") is not YamlSequenceNode rules)
        {
            throw new SopsConfigException(".sops.yaml has no creation_rules.");
        }

        foreach (var node in rules.Children)
        {
            if (node is not YamlMappingNode rule)
            {
                throw new SopsConfigException("Each creation rule in .sops.yaml must be a mapping.");
            }

            if (Matches(rule, path))
            {
                return Read(rule, path);
            }
        }

        throw new SopsConfigException($"No creation rule in .sops.yaml matches '{path}'.");
    }

    private static bool Matches(YamlMappingNode rule, StackFilePath path)
    {
        if (Scalar(rule, "path_regex") is not { Length: > 0 } pattern)
        {
            return true;
        }

        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout)
                .IsMatch(path.Value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new SopsConfigException($"path_regex '{pattern}' in .sops.yaml is not supported: {ex.Message}");
        }
    }

    private static SopsEncryptionRule Read(YamlMappingNode rule, StackFilePath path)
    {
        foreach (var (keyNode, value) in rule.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            if (key is "path_regex" or "age" || ValueKeys.Contains(key) || IsEmpty(value))
            {
                continue;
            }

            throw new SopsConfigException(
                $"The rule for '{path}' in .sops.yaml uses '{key}'; the aggregator encrypts for age recipients only. " +
                "Encrypt such files with local sops (git config forest.localSops true).");
        }

        var recipients = Recipients(Find(rule, "age"));
        if (recipients.Count == 0)
        {
            throw new SopsConfigException($"The rule for '{path}' in .sops.yaml has no age recipients.");
        }

        return new SopsEncryptionRule(
            recipients,
            Scalar(rule, "encrypted_regex"),
            Scalar(rule, "unencrypted_regex"),
            Scalar(rule, "encrypted_suffix"),
            Scalar(rule, "unencrypted_suffix"),
            Scalar(rule, "encrypted_comment_regex"),
            Scalar(rule, "unencrypted_comment_regex"),
            Scalar(rule, "mac_only_encrypted") is { } macOnly && bool.TryParse(macOnly, out var flag) && flag);
    }

    // age: "age1…,age1…" as sops writes it, or a YAML list.
    private static List<string> Recipients(YamlNode? node)
    {
        var values = node switch
        {
            YamlScalarNode scalar => (scalar.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => (s.Value ?? "").Trim()),
            _ => [],
        };

        var recipients = new List<string>();
        foreach (var value in values.Where(v => v.Length > 0))
        {
            if (!AgeRecipient().IsMatch(value))
            {
                throw new SopsConfigException($"'{value}' in .sops.yaml is not an age recipient (age1…).");
            }

            recipients.Add(value);
        }

        return recipients;
    }

    private static bool IsEmpty(YamlNode value) => value switch
    {
        YamlScalarNode scalar => string.IsNullOrWhiteSpace(scalar.Value),
        YamlSequenceNode sequence => sequence.Children.Count == 0,
        YamlMappingNode mapping => mapping.Children.Count == 0,
        _ => false,
    };

    private static string? Scalar(YamlMappingNode mapping, string key) => (Find(mapping, key) as YamlScalarNode)?.Value;

    private static YamlNode? Find(YamlMappingNode mapping, string key) =>
        mapping.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: var k } && k == key).Value;

    [GeneratedRegex(@"\Aage1[02-9ac-hj-np-z]{58}\z")]
    private static partial Regex AgeRecipient();
}
