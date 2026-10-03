using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Environments;

/// <summary>
/// An age key pair that decrypts the SOPS files deployed to one environment. A separate pair per environment isolates
/// environments cryptographically: a file encrypted for one environment's key cannot be read by any other, even from
/// a shared repository.
/// </summary>
/// <remarks>
/// The private part (the identity) exists only in the aggregator and is stored already encrypted by the application
/// layer; agents never receive it. The recipient is public: it goes into the repository's <c>.sops.yaml</c>.
/// An environment may have several keys while files are re-encrypted after a rotation; every one of them is tried.
/// </remarks>
public sealed partial class AgeKey : Entity<AgeKeyId>
{
    private AgeKey(AgeKeyId id, EnvironmentId environmentId, string recipient, string protectedIdentity, DateTimeOffset createdAt)
        : base(id)
    {
        EnvironmentId = environmentId;
        Recipient = recipient;
        ProtectedIdentity = protectedIdentity;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private AgeKey()
    {
        Recipient = null!;
        ProtectedIdentity = null!;
    }

    public EnvironmentId EnvironmentId { get; private init; }

    /// <summary>The public key, "age1…" in Bech32.</summary>
    public string Recipient { get; private init; }

    /// <summary>The encrypted "AGE-SECRET-KEY-1…".</summary>
    public string ProtectedIdentity { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <exception cref="DomainValidationException">The recipient is not an X25519 age recipient.</exception>
    public static AgeKey Create(EnvironmentId environmentId, string recipient, string protectedIdentity, DateTimeOffset now)
    {
        if (!RecipientPattern().IsMatch(recipient))
        {
            throw new DomainValidationException($"'{recipient}' is not an age recipient (age1… in lowercase Bech32).");
        }

        if (string.IsNullOrEmpty(protectedIdentity))
        {
            throw new DomainValidationException("The age identity must not be empty.");
        }

        return new AgeKey(AgeKeyId.New(), environmentId, recipient, protectedIdentity, now);
    }

    // "age1" and 58 Bech32 characters: a 32-byte X25519 key and a 6-character checksum.
    [GeneratedRegex(@"^age1[02-9ac-hj-np-z]{58}\z")]
    private static partial Regex RecipientPattern();
}
