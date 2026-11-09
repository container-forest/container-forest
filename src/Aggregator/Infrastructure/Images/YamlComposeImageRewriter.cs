using System.Text;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Images;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ContainerForest.Aggregator.Infrastructure.Images;

/// <summary>
/// Changes "image" values of a compose file by the positions the YAML parser reports, not by serializing the tree again:
/// comments, order, quotes and every other byte stay as they were (ADR-0015, item 4). Only a value written directly in
/// its service is changed; one that comes from an anchor, an alias or a merge key could be shared with other places,
/// and is refused rather than guessed.
/// </summary>
internal sealed class YamlComposeImageRewriter : IComposeImageRewriter
{
    private const string MergeKey = "<<";

    public IReadOnlyList<ComposeImageSlot> Find(string yaml, ImagePolicy policy) =>
        Locate(yaml, policy).Select(s => s.Slot).ToList();

    public ComposeRewrite Rewrite(string yaml, ImagePolicy policy, string value)
    {
        var located = Locate(yaml, policy);
        var replacements = new List<(Located At, string NewValue)>();
        var changes = new List<ImageChange>();
        foreach (var at in located)
        {
            var reference = Parse(at.Slot.Value);
            var newValue = $"{reference.Name}:{value}";
            if (!ImageReference.TryParse(newValue, out _))
            {
                throw new ImagePolicyException($"'{newValue}' is not an image reference.");
            }

            replacements.Add((at, newValue));
            if (newValue != at.Slot.Value)
            {
                changes.Add(new ImageChange(at.Slot.Service, at.Slot.Value, newValue));
            }
        }

        var result = Replace(yaml, replacements);
        Verify(yaml, result, changes);
        return new ComposeRewrite(result, changes);
    }

    public string? Apply(string yaml, IReadOnlyList<ImageChange> changes)
    {
        Dictionary<string, Located> slots;
        try
        {
            var wanted = changes.Select(c => c.Service).ToHashSet(StringComparer.Ordinal);
            slots = Services(Load(yaml))
                .Where(s => wanted.Contains(s.Name) && s.Image is not null)
                .Select(s => Slot(s.Name, s.Image!, s.ImageKey!))
                .ToDictionary(s => s.Slot.Service, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ImagePolicyException or YamlException)
        {
            return null;
        }

        var replacements = new List<(Located At, string NewValue)>();
        foreach (var change in changes)
        {
            if (!slots.TryGetValue(change.Service, out var at) || at.Slot.Value != change.From || !ImageReference.TryParse(change.To, out _))
            {
                return null;
            }

            replacements.Add((at, change.To));
        }

        return replacements.Select(r => r.At.Slot.Service).Distinct(StringComparer.Ordinal).Count() == replacements.Count
            ? Replace(yaml, replacements)
            : null;
    }

    private List<Located> Locate(string yaml, ImagePolicy policy)
    {
        YamlMappingNode root;
        try
        {
            root = Load(yaml);
        }
        catch (YamlException ex)
        {
            throw new ImagePolicyException($"The compose file is not valid YAML (line {ex.Start.Line}): {ex.Message}");
        }

        var located = new List<Located>();
        foreach (var service in Services(root))
        {
            if (!policy.Covers(service.Name))
            {
                continue;
            }

            if (service.Image is null)
            {
                if (FindMerged(service.Mapping, "image") is YamlScalarNode merged && Matches(merged.Value, policy))
                {
                    throw new ImagePolicyException(
                        $"Service '{service.Name}' takes its image from a merge key ('<<'); write 'image' in the service itself.");
                }

                continue;
            }

            if (service.Image is not YamlScalarNode scalar)
            {
                continue;
            }

            var value = scalar.Value ?? "";
            if (value.Contains('$', StringComparison.Ordinal))
            {
                var withoutVariables = value[..value.IndexOf('$', StringComparison.Ordinal)].TrimEnd(':', '@');
                if (Matches(withoutVariables, policy))
                {
                    throw new ImagePolicyException(
                        $"The image of service '{service.Name}' uses variables ({value}); the aggregator cannot rewrite it.");
                }

                continue;
            }

            if (Matches(value, policy))
            {
                located.Add(Slot(service.Name, scalar, service.ImageKey!));
            }
        }

        return located.Count > 0
            ? located
            : throw new ImagePolicyException($"No service of the compose file uses {policy.Image}.");
    }

    private static Located Slot(string service, YamlNode node, YamlNode key)
    {
        if (node is not YamlScalarNode scalar)
        {
            throw new ImagePolicyException($"The image of service '{service}' is not a plain value.");
        }

        // An alias resolves to the node where its anchor is, which YAML puts before every alias: a value that starts
        // before its own key is somewhere else, and changing it there would change every other place that uses it.
        if (!scalar.Anchor.IsEmpty || scalar.Start.Index < key.End.Index)
        {
            throw new ImagePolicyException(
                $"The image of service '{service}' is shared through a YAML anchor or alias; write it in the service itself.");
        }

        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) || !scalar.Tag.IsEmpty)
        {
            throw new ImagePolicyException($"The image of service '{service}' is a block or tagged value; write it on one line.");
        }

        var value = scalar.Value ?? "";
        var reference = Parse(value);
        return new Located(new ComposeImageSlot(service, value, reference.Tag, reference.Digest), (int)scalar.Start.Index, (int)scalar.End.Index, scalar.Style);
    }

    private static string Replace(string yaml, IEnumerable<(Located At, string NewValue)> replacements)
    {
        var builder = new StringBuilder(yaml);
        foreach (var (at, newValue) in replacements.OrderByDescending(r => r.At.Start))
        {
            var quoted = at.Style switch
            {
                ScalarStyle.SingleQuoted => $"'{newValue}'",
                ScalarStyle.DoubleQuoted => $"\"{newValue}\"",
                _ => newValue,
            };
            builder.Remove(at.Start, at.End - at.Start).Insert(at.Start, quoted);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the result back: the same services, the changed ones with their new values and every other image as it was.
    /// A position that pointed elsewhere than the parser said would show up here instead of in a commit.
    /// </summary>
    private static void Verify(string before, string after, IReadOnlyList<ImageChange> changes)
    {
        Dictionary<string, string?> Images(string yaml) =>
            Services(Load(yaml)).ToDictionary(s => s.Name, s => (s.Image as YamlScalarNode)?.Value, StringComparer.Ordinal);

        var old = Images(before);
        var updated = Images(after);
        var expected = new Dictionary<string, string?>(old, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            expected[change.Service] = change.To;
        }

        if (updated.Count != expected.Count || expected.Any(e => !updated.TryGetValue(e.Key, out var v) || v != e.Value))
        {
            throw new ImagePolicyException("Rewriting the compose file changed more than the image values; nothing is committed.");
        }
    }

    private static bool Matches(string? value, ImagePolicy policy) =>
        ImageReference.TryParse(value, out var reference) && ImageName.TryFrom(reference, out var name) && name == policy.Image;

    private static ImageReference Parse(string value) =>
        ImageReference.TryParse(value, out var reference)
            ? reference
            : throw new ImagePolicyException($"'{value}' is not an image reference.");

    private static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ImagePolicyException("The compose file must be one YAML mapping with a 'services' section.");
        }

        return root;
    }

    private static IEnumerable<ServiceNode> Services(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode services)
        {
            throw new ImagePolicyException("The compose file has no 'services' section written in it.");
        }

        foreach (var (key, value) in services.Children)
        {
            if (key is YamlScalarNode { Value: { } name } && value is YamlMappingNode mapping)
            {
                var imageKey = mapping.Children.Keys.FirstOrDefault(k => k is YamlScalarNode { Value: "image" });
                yield return new ServiceNode(name, mapping, imageKey is null ? null : mapping.Children[imageKey], imageKey);
            }
        }
    }

    private static YamlNode? FindMerged(YamlMappingNode mapping, string key)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(MergeKey), out var merged))
        {
            return null;
        }

        var sources = merged switch
        {
            YamlMappingNode single => [single],
            YamlSequenceNode many => many.Children.OfType<YamlMappingNode>().ToList(),
            _ => new List<YamlMappingNode>(),
        };
        return sources
            .Select(source => source.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : FindMerged(source, key))
            .FirstOrDefault(found => found is not null);
    }

    private sealed record ServiceNode(string Name, YamlMappingNode Mapping, YamlNode? Image, YamlNode? ImageKey);

    /// <param name="Start">Character offset of the scalar, quotes included.</param>
    /// <param name="End">Character offset just after it.</param>
    private sealed record Located(ComposeImageSlot Slot, int Start, int End, ScalarStyle Style);
}
