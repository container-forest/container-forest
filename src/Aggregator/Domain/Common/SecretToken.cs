using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ContainerForest.Aggregator.Domain.Common;

/// <summary>
/// Random bearer secrets (enrollment tokens, agent secrets) and their stored hashes.
/// </summary>
/// <remarks>
/// Tokens carry 256 bits of entropy, so a single unsalted SHA-256 is enough: brute force is infeasible,
/// and a deterministic hash lets the aggregator find an environment by the enrollment token alone.
/// User passwords are not handled here; ASP.NET Core Identity hashes them.
/// </remarks>
public static class SecretToken
{
    private const int TokenBytes = 32;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Compares in constant time so the check does not leak how many bytes matched.</summary>
    public static bool Matches(string token, string hash)
    {
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), actual);

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        var status = Convert.FromHexString(hash, expected, out _, out var written);
        if (status != System.Buffers.OperationStatus.Done || written != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
