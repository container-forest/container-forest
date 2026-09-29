using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Stacks;

/// <summary>
/// Stack name, used as the compose project name and as the stack directory name on the host.
/// The format is what compose accepts for a project name; it also rules out path traversal.
/// </summary>
public sealed partial record StackName
{
    public const int MaxLength = 63;

    private StackName(string value) => Value = value;

    public string Value { get; }

    public static StackName Parse(string value) =>
        TryParse(value, out var name)
            ? name
            : throw new DomainValidationException(
                $"Stack name must be 1-{MaxLength} characters of a-z, 0-9, '_' or '-', starting with a letter or digit.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out StackName? name)
    {
        name = value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value) ? new StackName(value) : null;
        return name is not null;
    }

    public override string ToString() => Value;

    // \z instead of $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
