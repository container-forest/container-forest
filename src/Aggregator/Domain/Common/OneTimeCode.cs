using System.Security.Cryptography;

namespace ContainerForest.Aggregator.Domain.Common;

/// <summary>
/// Short codes a person reads off one screen and types into another: the Discord link code, the CLI's user code.
/// Only their hashes are stored.
/// </summary>
/// <remarks>
/// A code is short, so its hash alone does not stop a guess; what does is its short lifetime and a single use.
/// </remarks>
public static class OneTimeCode
{
    // No look-alikes (0/O, 1/I/L), so a code read off the screen is typed correctly.
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    /// <summary>The code as the user may type it: case, spaces and dashes do not matter.</summary>
    public static string Normalize(string code) =>
        new(code.Where(c => c is not (' ' or '-')).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string code) => SecretToken.Hash(Normalize(code));
}
