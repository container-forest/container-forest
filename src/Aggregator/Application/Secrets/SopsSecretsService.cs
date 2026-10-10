using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Secrets;

/// <summary>
/// Decrypts and encrypts SOPS files for the git filter of the CLI (ADR-0010), so people work with plain text in their
/// clones while the repository keeps standard sops files.
/// </summary>
/// <remarks>
/// Decryption is limited cryptographically: the aggregator tries only the keys of environments where the caller is an
/// Editor (of the environment or of one of its stacks), the same people who can deploy the file and read it on the
/// host anyway. Nothing the client says besides the file itself is trusted: there is no repository to bind it to.
/// Encryption needs no private key, so any caller who may change things may encrypt.
/// </remarks>
public sealed partial class SopsSecretsService(
    CurrentActor currentActor,
    IRoleAssignmentRepository roleAssignments,
    IAgeKeyRepository keys,
    ISecretProtector protector,
    ISopsDecryptor decryptor,
    ISopsEncryptor encryptor,
    ISopsConfigReader configs,
    ILogger<SopsSecretsService> logger)
{
    /// <summary>Larger than any reasonable secrets file, small enough to keep in memory per request.</summary>
    public const int MaxContentBytes = 1024 * 1024;

    public const int MaxConfigBytes = 64 * 1024;

    /// <param name="path">The file's path in the repository; its extension tells the format.</param>
    /// <exception cref="AccessDeniedException">The token may not decrypt secrets, or no recipient of the file is a key of an environment where the caller is an Editor.</exception>
    /// <exception cref="DomainValidationException">The content is not a SOPS file or is too large.</exception>
    /// <exception cref="SopsDecryptionException">No key of this aggregator is a recipient, or sops failed.</exception>
    public async Task<byte[]> DecryptAsync(StackFilePath path, byte[] content, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        if (!actor.Scope.CanDecryptSecrets)
        {
            throw new AccessDeniedException(
                "This API token may not decrypt secrets. Create one with the right to decrypt secrets " +
                "(forest login --secrets) or use the panel.");
        }

        EnsureSize(content);
        if (!SopsFile.IsEncrypted(path, content))
        {
            throw new DomainValidationException($"'{path}' is not a SOPS-encrypted file.");
        }

        var recipientKeys = await keys.FindByRecipientsAsync(SopsFile.AgeRecipients(content).ToList(), cancellationToken);
        if (recipientKeys.Count == 0)
        {
            throw new SopsDecryptionException(
                "no environment's age key is a recipient of the file; only its own recipients can decrypt it.");
        }

        var usable = actor.IsAdmin ? recipientKeys : await EditableAsync(actor, recipientKeys, cancellationToken);
        if (usable.Count == 0)
        {
            throw new AccessDeniedException(
                "The file is encrypted for environments where you are not an Editor; only Editors decrypt their secrets.");
        }

        var plain = await decryptor.DecryptAsync(path, content, usable.Select(Unprotect).ToList(), cancellationToken);
        LogDecrypted(actor.UserName, path.Value, currentActor.ApiTokenId?.ToString() ?? "session");
        return plain;
    }

    /// <param name="config">The repository's <c>.sops.yaml</c>: the first rule matching <paramref name="path"/> applies.</param>
    /// <exception cref="AccessDeniedException">The token is read-only.</exception>
    /// <exception cref="DomainValidationException">The content is already encrypted, or the content or config is too large.</exception>
    /// <exception cref="SopsConfigException">No usable creation rule for the path.</exception>
    /// <exception cref="SopsEncryptionException">sops failed.</exception>
    public async Task<byte[]> EncryptAsync(StackFilePath path, byte[] content, string config, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        if (actor.Scope.IsReadOnly)
        {
            throw new AccessDeniedException("A read-only token cannot encrypt secrets.");
        }

        EnsureSize(content);
        if (System.Text.Encoding.UTF8.GetByteCount(config) > MaxConfigBytes)
        {
            throw new DomainValidationException($".sops.yaml must be at most {MaxConfigBytes / 1024} KiB.");
        }

        if (SopsFile.IsEncrypted(path, content))
        {
            throw new DomainValidationException($"'{path}' is already encrypted.");
        }

        var rule = configs.RuleFor(config, path);
        return await encryptor.EncryptAsync(path, content, rule, cancellationToken);
    }

    private async Task<IReadOnlyList<AgeKey>> EditableAsync(
        Actor actor, IReadOnlyList<AgeKey> recipientKeys, CancellationToken cancellationToken)
    {
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        var editable = AccessPolicy.EditorEnvironments(actor, assignments);
        return recipientKeys.Where(k => editable.Contains(k.EnvironmentId)).ToList();
    }

    private static void EnsureSize(byte[] content)
    {
        if (content.Length > MaxContentBytes)
        {
            throw new DomainValidationException($"A secrets file must be at most {MaxContentBytes / 1024} KiB.");
        }
    }

    private string Unprotect(AgeKey key)
    {
        try
        {
            return protector.Unprotect(SecretPurpose.AgeIdentity, key.ProtectedIdentity);
        }
        catch (SecretUnreadableException)
        {
            throw new SopsDecryptionException(
                $"the private part of age key {key.Recipient} cannot be decrypted (the aggregator's key ring was lost or changed).");
        }
    }

    // There is no audit log yet; until then the aggregator's log records who decrypted which file, never the content.
    [LoggerMessage(Level = LogLevel.Information, Message = "{User} decrypted {Path} with {Credentials}")]
    private partial void LogDecrypted(string user, string path, string credentials);
}
