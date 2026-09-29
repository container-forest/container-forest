using System.Diagnostics.CodeAnalysis;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Stacks;

/// <summary>Full git commit id: 40 hex characters (SHA-1) or 64 (SHA-256), stored in lower case.</summary>
public sealed record CommitSha
{
    private CommitSha(string value) => Value = value;

    public string Value { get; }

    /// <summary>First 7 characters, as git shows them.</summary>
    public string Short => Value[..7];

    public static CommitSha Parse(string value) =>
        TryParse(value, out var sha)
            ? sha
            : throw new DomainValidationException($"'{value}' is not a full git commit SHA.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out CommitSha? sha)
    {
        sha = value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit)
            ? new CommitSha(value.ToLowerInvariant())
            : null;
        return sha is not null;
    }

    public override string ToString() => Value;
}
