using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Secrets;

public interface IAgeKeyRepository : IRepository<AgeKey, AgeKeyId>
{
    /// <summary>Newest first: the first key is the one to encrypt new files for.</summary>
    Task<IReadOnlyList<AgeKey>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken);
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
