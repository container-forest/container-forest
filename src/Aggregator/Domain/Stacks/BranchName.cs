using System.Diagnostics.CodeAnalysis;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Stacks;

/// <summary>Git branch name, validated against the main rules of git check-ref-format.</summary>
public sealed record BranchName
{
    public const int MaxLength = 255;

    private static readonly char[] ForbiddenChars = ['~', '^', ':', '?', '*', '[', '\\', ' '];

    private BranchName(string value) => Value = value;

    public string Value { get; }

    public static BranchName Parse(string value) =>
        TryParse(value, out var branch)
            ? branch
            : throw new DomainValidationException($"'{value}' is not a valid git branch name.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out BranchName? branch)
    {
        branch = IsValid(value) ? new BranchName(value) : null;
        return branch is not null;
    }

    public override string ToString() => Value;

    private static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= MaxLength })
        {
            return false;
        }

        if (value.Any(char.IsControl) || value.IndexOfAny(ForbiddenChars) >= 0)
        {
            return false;
        }

        // A leading '-' could be read as an option by git tooling.
        if (value[0] is '-' or '/' || value[^1] is '/' or '.' || value.EndsWith(".lock", StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Contains("..", StringComparison.Ordinal)
            && !value.Contains("//", StringComparison.Ordinal)
            && !value.Contains("@{", StringComparison.Ordinal)
            && value != "@"
            && value.Split('/').All(segment => segment[0] != '.');
    }
}
