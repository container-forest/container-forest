using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// Checks codes of an authenticator app (TOTP, RFC 6238: HMAC-SHA1, 30-second steps, 6 digits) against the user's key,
/// one step of clock drift either way. Unlike Identity's own provider it refuses a code whose step was already used,
/// so a code seen over someone's shoulder or in a proxy log cannot sign in a second time.
/// </summary>
public sealed class TotpAuthenticator(TimeProvider time) : IUserTwoFactorTokenProvider<AccountUser>
{
    public const string ProviderName = "ForestAuthenticator";

    public const int Digits = 6;

    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>Where the last accepted step is kept, next to Identity's own tokens.</summary>
    public const string LoginProvider = "[ContainerForest]";

    public const string LastStepName = "LastTotpStep";

    /// <summary>The app makes the codes; the aggregator never sends one.</summary>
    public Task<string> GenerateAsync(string purpose, UserManager<AccountUser> manager, AccountUser user) =>
        throw new NotSupportedException("Authenticator codes come from the user's app.");

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<AccountUser> manager, AccountUser user)
    {
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key) || Base32.Decode(key) is not { } secret || !TryParseCode(token, out var code))
        {
            return false;
        }

        var current = StepAt(time.GetUtcNow());
        var lastUsed = long.TryParse(
            await manager.GetAuthenticationTokenAsync(user, LoginProvider, LastStepName), NumberStyles.None, CultureInfo.InvariantCulture, out var stored)
            ? stored
            : long.MinValue;
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step > lastUsed && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                await manager.SetAuthenticationTokenAsync(user, LoginProvider, LastStepName, step.ToString(CultureInfo.InvariantCulture));
                return true;
            }
        }

        return false;
    }

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AccountUser> manager, AccountUser user) =>
        !string.IsNullOrEmpty(await manager.GetAuthenticatorKeyAsync(user));

    /// <summary>Six digits; spaces the app shows between groups are allowed.</summary>
    public static bool TryParseCode(string token, out string code)
    {
        code = token.Replace(" ", "", StringComparison.Ordinal).Trim();
        return code.Length == Digits && code.All(char.IsAsciiDigit);
    }

    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>The code for <paramref name="step"/> (RFC 4226 dynamic truncation).</summary>
    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>otpauth://</c> link apps read from the QR code.</summary>
    public static string Uri(string issuer, string userName, string key) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(userName)}" +
        $"?secret={key}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={(int)Step.TotalSeconds}";
}

/// <summary>RFC 4648 base32, the alphabet of authenticator keys; Identity's own decoder is internal.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <returns>Null for a character outside the alphabet.</returns>
    public static byte[]? Decode(string value)
    {
        var text = value.Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var bytes = new byte[text.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in text)
        {
            var digit = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (digit < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return bytes;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
