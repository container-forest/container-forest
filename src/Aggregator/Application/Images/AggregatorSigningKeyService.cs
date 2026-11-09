using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Images;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>
/// The aggregator's own signing key for update commits (ADR-0015, item 6): created when first needed, shown to
/// administrators so they can add it to git servers, and rotated by them. It never endorses content.
/// </summary>
public sealed class AggregatorSigningKeyService(
    CurrentActor currentActor,
    IAggregatorSigningKeyRepository keys,
    ISshKeyGenerator generator,
    ISecretProtector protector,
    ImageUpdateLocks locks,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    public const string KeyComment = "container-forest";

    /// <summary>The active key and the retired ones, newest first; nothing is created by looking.</summary>
    public async Task<IReadOnlyList<AggregatorSigningKey>> ListAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await keys.ListAsync(cancellationToken);
    }

    /// <summary>Replaces the active key with a new one; commits signed before stay endorsed by their records.</summary>
    public Task<AggregatorSigningKey> RotateAsync(CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.AggregatorSigningKeyRotated, new SettingsAuditTarget("SigningKey"));
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            await locks.SigningKey.WaitAsync(cancellationToken);
            try
            {
                var now = time.GetUtcNow();
                (await keys.FindActiveAsync(cancellationToken))?.Retire(now);
                var key = await GenerateAsync(now, cancellationToken);
                draft.Details = key.Fingerprint;
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return key;
            }
            finally
            {
                locks.SigningKey.Release();
            }
        }, cancellationToken);
    }

    /// <summary>The active key with its private part decrypted, created first when there is none. For the update pipeline.</summary>
    /// <exception cref="ImagePolicyException">The stored key cannot be decrypted.</exception>
    public async Task<(AggregatorSigningKey Key, string PrivateKey)> GetActiveAsync(CancellationToken cancellationToken)
    {
        await locks.SigningKey.WaitAsync(cancellationToken);
        try
        {
            var key = await keys.FindActiveAsync(cancellationToken);
            if (key is null)
            {
                key = await GenerateAsync(time.GetUtcNow(), cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            try
            {
                return (key, protector.Unprotect(SecretPurpose.AggregatorSigningKey, key.ProtectedPrivateKey));
            }
            catch (SecretUnreadableException)
            {
                throw new ImagePolicyException(
                    "The aggregator's signing key cannot be decrypted (its key ring was lost); an administrator rotates it.");
            }
        }
        finally
        {
            locks.SigningKey.Release();
        }
    }

    private async Task<AggregatorSigningKey> GenerateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var generated = await generator.GenerateAsync(KeyComment, cancellationToken);
        var fingerprint = SshPublicKey.Parse(generated.PublicKey).Fingerprint;
        var key = AggregatorSigningKey.Create(
            protector.Protect(SecretPurpose.AggregatorSigningKey, generated.PrivateKey), generated.PublicKey, fingerprint, now);
        keys.Add(key);
        return key;
    }
}
