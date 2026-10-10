using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Secrets;

public interface IAgeKeyRepository : IRepository<AgeKey, AgeKeyId>
{
    /// <summary>Newest first: the first key is the one to encrypt new files for.</summary>
    Task<IReadOnlyList<AgeKey>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>The keys, of any environment, whose public keys are among <paramref name="recipients"/>.</summary>
    Task<IReadOnlyList<AgeKey>> FindByRecipientsAsync(IReadOnlyCollection<string> recipients, CancellationToken cancellationToken);
}

/// <summary>Creates age key pairs. Implemented with the age-keygen binary in the infrastructure layer.</summary>
public interface IAgeKeyGenerator
{
    Task<GeneratedAgeKey> GenerateAsync(CancellationToken cancellationToken);
}

/// <param name="Recipient">The public key, "age1…".</param>
/// <param name="Identity">The private key, "AGE-SECRET-KEY-1…"; it is encrypted before it is stored.</param>
public sealed record GeneratedAgeKey(string Recipient, string Identity)
{
    // Records print every property; the private key must not reach a log through ToString.
    public override string ToString() => $"{nameof(GeneratedAgeKey)} {{ Recipient = {Recipient} }}";
}

/// <summary>Decrypts SOPS files. Implemented with the sops binary in the infrastructure layer.</summary>
public interface ISopsDecryptor
{
    /// <param name="path">Where the file lies in the stack; its extension tells the format, as for sops itself.</param>
    /// <param name="identities">The environment's private keys; any of them may be a recipient of the file.</param>
    /// <exception cref="SopsDecryptionException">The file cannot be decrypted with these keys or is damaged.</exception>
    Task<byte[]> DecryptAsync(
        StackFilePath path, byte[] content, IReadOnlyList<string> identities, CancellationToken cancellationToken);
}

/// <summary>Why a file could not be decrypted, readable by the user; it never contains the file's content.</summary>
public sealed class SopsDecryptionException(string message) : Exception(message);

/// <summary>Encrypts SOPS files for age recipients. Implemented with the sops binary in the infrastructure layer.</summary>
public interface ISopsEncryptor
{
    /// <param name="path">Where the file lies in the repository; its extension tells the format, as for sops itself.</param>
    /// <exception cref="SopsEncryptionException">sops could not encrypt the content, e.g. it is not valid YAML.</exception>
    Task<byte[]> EncryptAsync(StackFilePath path, byte[] content, SopsEncryptionRule rule, CancellationToken cancellationToken);
}

/// <summary>Reads a repository's <c>.sops.yaml</c>. Implemented with YamlDotNet in the infrastructure layer.</summary>
public interface ISopsConfigReader
{
    /// <summary>The first creation rule whose <c>path_regex</c> matches the path, as sops chooses it.</summary>
    /// <exception cref="SopsConfigException">The file is not valid, no rule matches, or the rule needs more than age.</exception>
    SopsEncryptionRule RuleFor(string config, StackFilePath path);
}

/// <summary>
/// The part of a <c>.sops.yaml</c> creation rule the aggregator encrypts with: age recipients only, and which values
/// to encrypt. Other key types (KMS, Vault, PGP) would make the aggregator call out to services named by the
/// repository, so a rule that needs them is refused; such files are encrypted with local sops.
/// </summary>
public sealed record SopsEncryptionRule(
    IReadOnlyList<string> AgeRecipients,
    string? EncryptedRegex = null,
    string? UnencryptedRegex = null,
    string? EncryptedSuffix = null,
    string? UnencryptedSuffix = null,
    string? EncryptedCommentRegex = null,
    string? UnencryptedCommentRegex = null,
    bool MacOnlyEncrypted = false);

/// <summary>Why a <c>.sops.yaml</c> cannot be used, readable by the user.</summary>
public sealed class SopsConfigException(string message) : Exception(message);

/// <summary>Why a file could not be encrypted, readable by the user; it never contains the file's content.</summary>
public sealed class SopsEncryptionException(string message) : Exception(message);
