using System.Buffers.Binary;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A public SSH key in the OpenSSH format ("ssh-ed25519 AAAA… comment"), as git uses it to sign commits
/// (<c>gpg.format=ssh</c>, ADR-0011). Only the algorithms OpenSSH still recommends are accepted.
/// </summary>
public sealed record SshPublicKey
{
    public const string Ed25519 = "ssh-ed25519";
    public const string SkEd25519 = "sk-ssh-ed25519@openssh.com";
    public const string SkEcdsaP256 = "sk-ecdsa-sha2-nistp256@openssh.com";
    public const string Rsa = "ssh-rsa";

    /// <summary>Smaller RSA keys are refused; NIST SP 800-57 allows 2048 bits until 2030.</summary>
    public const int MinRsaBits = 2048;

    private static readonly Dictionary<string, string> EcdsaCurves = new(StringComparer.Ordinal)
    {
        ["ecdsa-sha2-nistp256"] = "nistp256",
        ["ecdsa-sha2-nistp384"] = "nistp384",
        ["ecdsa-sha2-nistp521"] = "nistp521",
    };

    private SshPublicKey(string algorithm, byte[] blob)
    {
        Algorithm = algorithm;
        Blob = blob;
        Fingerprint = FingerprintOf(blob);
    }

    /// <summary>Key type, e.g. "ssh-ed25519".</summary>
    public string Algorithm { get; }

    /// <summary>The key in the SSH wire format; what the signature and the fingerprint cover.</summary>
    public byte[] Blob { get; }

    /// <summary>"SHA256:…", as <c>ssh-keygen -l</c> shows it.</summary>
    public string Fingerprint { get; }

    /// <summary>The private key lives on a FIDO authenticator: signing needs a touch, and it cannot be copied.</summary>
    public bool IsHardwareBacked => Algorithm is SkEd25519 or SkEcdsaP256;

    /// <summary>"type base64", without the comment: the form kept and written to allowed_signers.</summary>
    public string OpenSsh => $"{Algorithm} {Convert.ToBase64String(Blob)}";

    /// <exception cref="DomainValidationException">Not a public key in the OpenSSH format, or an algorithm that is not accepted.</exception>
    public static SshPublicKey Parse(string value)
    {
        var parts = value.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new DomainValidationException(
                "Expected a public SSH key such as 'ssh-ed25519 AAAA… comment', the content of the .pub file.");
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new DomainValidationException("The public SSH key is not valid base64.");
        }

        var key = FromBlob(blob);
        if (key.Algorithm != parts[0])
        {
            throw new DomainValidationException($"The key says '{parts[0]}' but contains a '{key.Algorithm}' key.");
        }

        return key;
    }

    /// <summary>A key from its wire form, e.g. the one embedded in a signature.</summary>
    /// <exception cref="DomainValidationException">Not a well-formed key of an accepted algorithm.</exception>
    public static SshPublicKey FromBlob(byte[] blob)
    {
        var reader = new SshReader(blob);
        if (!reader.TryReadString(out var algorithmBytes))
        {
            throw new DomainValidationException("The public SSH key is malformed.");
        }

        var algorithm = Encoding.ASCII.GetString(algorithmBytes);
        var valid = algorithm switch
        {
            Ed25519 => reader.TryReadString(out var point) && point.Length == 32,
            SkEd25519 => reader.TryReadString(out var point) && point.Length == 32 && reader.TryReadString(out _),
            SkEcdsaP256 => reader.TryReadString(out var curve) && Encoding.ASCII.GetString(curve) == "nistp256"
                && reader.TryReadString(out _) && reader.TryReadString(out _),
            Rsa => reader.TryReadString(out _) && reader.TryReadString(out var modulus) && RsaBits(modulus) >= MinRsaBits,
            _ when EcdsaCurves.TryGetValue(algorithm, out var expected) =>
                reader.TryReadString(out var curve) && Encoding.ASCII.GetString(curve) == expected && reader.TryReadString(out _),
            _ => throw new DomainValidationException(
                $"SSH keys of type '{algorithm}' are not accepted; use ssh-ed25519 (or a FIDO key, sk-ssh-ed25519)."),
        };

        if (!valid || !reader.AtEnd)
        {
            throw new DomainValidationException(algorithm == Rsa
                ? $"RSA keys must be well-formed and at least {MinRsaBits} bits."
                : "The public SSH key is malformed.");
        }

        return new SshPublicKey(algorithm, blob);
    }

    public static string FingerprintOf(byte[] blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    public bool Equals(SshPublicKey? other) => other is not null && Fingerprint == other.Fingerprint;

    public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => $"{Algorithm} {Fingerprint}";

    private static int RsaBits(byte[] modulus)
    {
        // An mpint carries a leading zero byte when its top bit is set.
        var span = modulus.AsSpan();
        while (span.Length > 0 && span[0] == 0)
        {
            span = span[1..];
        }

        return span.Length == 0 ? 0 : (span.Length - 1) * 8 + (32 - BitOperations.LeadingZeroCount(span[0]));
    }
}

/// <summary>
/// An SSH signature (the SSHSIG format of OpenSSH's PROTOCOL.sshsig), as git stores it in the "gpgsig" header of a
/// signed commit. Parsing tells which key claims to have signed; whether it really did, only <c>ssh-keygen -Y verify</c> says.
/// </summary>
public sealed record SshSignature
{
    /// <summary>The namespace git signs commits and tags in; a signature made for another purpose is not a commit signature.</summary>
    public const string GitNamespace = "git";

    private const string Begin = "-----BEGIN SSH SIGNATURE-----";
    private const string End = "-----END SSH SIGNATURE-----";

    private SshSignature(string armored, SshPublicKey publicKey, string @namespace, string hashAlgorithm)
    {
        Armored = armored;
        PublicKey = publicKey;
        Namespace = @namespace;
        HashAlgorithm = hashAlgorithm;
    }

    /// <summary>The signature as git stores it, between the BEGIN and END lines.</summary>
    public string Armored { get; }

    public SshPublicKey PublicKey { get; }

    public string Namespace { get; }

    /// <summary>"sha256" or "sha512".</summary>
    public string HashAlgorithm { get; }

    /// <summary>True when the text looks like an SSH signature at all, as opposed to a GPG or X.509 one.</summary>
    public static bool IsSshSignature(string value) => value.TrimStart().StartsWith(Begin, StringComparison.Ordinal);

    /// <summary>Parses an armored signature; false for anything else, including a key of an algorithm that is not accepted.</summary>
    public static bool TryParse(string value, [NotNullWhen(true)] out SshSignature? signature)
    {
        signature = null;
        var text = value.Replace("\r", "", StringComparison.Ordinal).Trim();
        if (!text.StartsWith(Begin, StringComparison.Ordinal) || !text.EndsWith(End, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(text[Begin.Length..^End.Length].Replace("\n", "", StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return false;
        }

        var reader = new SshReader(blob);
        if (!reader.TryReadMagic("SSHSIG"u8)
            || !reader.TryReadUInt32(out var version) || version != 1
            || !reader.TryReadString(out var keyBlob)
            || !reader.TryReadString(out var @namespace)
            || !reader.TryReadString(out _)
            || !reader.TryReadString(out var hashAlgorithm)
            || !reader.TryReadString(out _)
            || !reader.AtEnd)
        {
            return false;
        }

        SshPublicKey key;
        try
        {
            key = SshPublicKey.FromBlob(keyBlob);
        }
        catch (DomainValidationException)
        {
            return false;
        }

        var hash = Encoding.ASCII.GetString(hashAlgorithm);
        if (hash is not ("sha256" or "sha512"))
        {
            return false;
        }

        signature = new SshSignature(text + "\n", key, Encoding.UTF8.GetString(@namespace), hash);
        return true;
    }
}

/// <summary>Reads the SSH wire encoding: uint32 big-endian lengths followed by bytes.</summary>
internal ref struct SshReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> rest = data;

    public readonly bool AtEnd => rest.IsEmpty;

    public bool TryReadUInt32(out uint value)
    {
        if (rest.Length < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(rest);
        rest = rest[4..];
        return true;
    }

    public bool TryReadString(out byte[] value)
    {
        value = [];
        if (!TryReadUInt32(out var length) || length > rest.Length)
        {
            return false;
        }

        value = rest[..(int)length].ToArray();
        rest = rest[(int)length..];
        return true;
    }

    public bool TryReadMagic(ReadOnlySpan<byte> magic)
    {
        if (!rest.StartsWith(magic))
        {
            return false;
        }

        rest = rest[magic.Length..];
        return true;
    }
}
