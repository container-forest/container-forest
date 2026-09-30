using System.Text.RegularExpressions;
using ContainerForest.Agent.Commands;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// Checks stack names before they become a compose project name and a directory name.
/// The same rule as the aggregator's: compose's own project name rule, which also rules out path traversal.
/// </summary>
public static partial class StackName
{
    public const int MaxLength = 63;

    /// <returns>The name, unchanged.</returns>
    /// <exception cref="InvalidCommandArgumentException">The name is empty, too long or has characters compose does not allow.</exception>
    public static string Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidCommandArgumentException("Stack name is required.");
        }

        if (name.Length > MaxLength || !Pattern().IsMatch(name))
        {
            throw new InvalidCommandArgumentException(
                $"Stack name must be at most {MaxLength} characters: lowercase letters, digits, '_' and '-', starting with a letter or digit.");
        }

        return name;
    }

    // \z instead of $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
