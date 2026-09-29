using System.Diagnostics.CodeAnalysis;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Stacks;

/// <summary>
/// Path of a file inside a stack (in the repository and in the stack directory on the host):
/// relative, '/'-separated, without "." or ".." segments.
/// </summary>
/// <remarks>
/// This is a syntactic check. The agent still resolves every path with Path.GetFullPath and
/// verifies it stays inside the stack directory before writing.
/// </remarks>
public sealed record StackFilePath
{
    public const int MaxLength = 255;

    private StackFilePath(string value) => Value = value;

    public string Value { get; }

    public static StackFilePath Parse(string value) =>
        TryParse(value, out var path)
            ? path
            : throw new DomainValidationException(
                $"'{value}' is not a valid stack file path: it must be relative, use '/' as the separator " +
                $"and contain no empty, '.' or '..' segments.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out StackFilePath? path)
    {
        path = IsValid(value) ? new StackFilePath(value) : null;
        return path is not null;
    }

    public override string ToString() => Value;

    private static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= MaxLength })
        {
            return false;
        }

        // Backslashes and ':' would be separators or drive letters on Windows development machines.
        if (value.Any(c => char.IsControl(c) || c is '\\' or ':'))
        {
            return false;
        }

        // A leading '/' yields an empty first segment, so absolute paths are rejected here too.
        return value.Split('/').All(segment => segment is not ("" or "." or ".."));
    }
}
