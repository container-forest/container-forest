using System.Text.RegularExpressions;

namespace ContainerForest.Agent.Commands;

/// <summary>Checks container ids from commands before they reach the Docker API.</summary>
public static partial class ContainerIds
{
    public const int MaxLength = 128;

    // The id becomes part of a Docker API path, so only characters of container ids and names are allowed.
    // A '/' or ".." could otherwise address a different endpoint.
    /// <exception cref="InvalidCommandArgumentException">The id is empty, too long or has other characters.</exception>
    public static string Validate(string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
        {
            throw new InvalidCommandArgumentException("Container id is required.");
        }

        if (containerId.Length > MaxLength || !Pattern().IsMatch(containerId))
        {
            throw new InvalidCommandArgumentException("Container id may contain only letters, digits, '_', '.' and '-'.");
        }

        return containerId;
    }

    // \z instead of $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
