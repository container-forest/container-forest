using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Images;

/// <summary>
/// The SSH key the aggregator signs its update commits with (ADR-0015). It never endorses anything: git servers and people
/// see the commits as signed, while the deployment pipeline endorses them by their <see cref="ImageUpdateRecord"/>.
/// One is active; retired ones are kept so their public parts can still be shown and added to git servers.
/// </summary>
public sealed class AggregatorSigningKey : Entity<AggregatorSigningKeyId>
{
    private AggregatorSigningKey(
        AggregatorSigningKeyId id, string protectedPrivateKey, string publicKey, string fingerprint, DateTimeOffset createdAt)
        : base(id)
    {
        ProtectedPrivateKey = protectedPrivateKey;
        PublicKey = publicKey;
        Fingerprint = fingerprint;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private AggregatorSigningKey()
    {
        ProtectedPrivateKey = null!;
        PublicKey = null!;
        Fingerprint = null!;
    }

    /// <summary>The OpenSSH private key, encrypted by the application layer.</summary>
    public string ProtectedPrivateKey { get; private init; }

    /// <summary>The public key in OpenSSH form, "ssh-ed25519 AAAA… container-forest".</summary>
    public string PublicKey { get; private init; }

    /// <summary>"SHA256:…", as ssh-keygen -l shows it.</summary>
    public string Fingerprint { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>When a rotation replaced it; null for the active key.</summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    public bool IsActive => RetiredAt is null;

    public static AggregatorSigningKey Create(string protectedPrivateKey, string publicKey, string fingerprint, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(protectedPrivateKey) || string.IsNullOrWhiteSpace(publicKey) || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw new DomainValidationException("A signing key needs its protected private part, its public part and a SHA256 fingerprint.");
        }

        return new AggregatorSigningKey(AggregatorSigningKeyId.New(), protectedPrivateKey, publicKey.Trim(), fingerprint, now);
    }

    public void Retire(DateTimeOffset now) => RetiredAt ??= now;
}
