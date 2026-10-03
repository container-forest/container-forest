using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Secrets;

/// <summary>An environment's age key without its private part, which never leaves the aggregator.</summary>
public sealed record AgeKeySummary(AgeKeyId Id, string Recipient, DateTimeOffset CreatedAt)
{
    public static AgeKeySummary Of(AgeKey key) => new(key.Id, key.Recipient, key.CreatedAt);
}

/// <summary>
/// The age keys SOPS files of an environment are encrypted for. Anyone who sees the environment reads the public keys,
/// to encrypt secrets for it; only administrators create and delete keys, as with registry credentials.
/// </summary>
public sealed class AgeKeyService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IRoleAssignmentRepository roleAssignments,
    IAgeKeyRepository keys,
    IAgeKeyGenerator generator,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Newest first; an invisible environment is not found.</summary>
    public async Task<IReadOnlyList<AgeKeySummary>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        if (environment is null
            || !AccessPolicy.CanSee(actor, environment, await roleAssignments.ListForUserAsync(actor.Id, cancellationToken)))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        return (await keys.ListByEnvironmentAsync(environmentId, cancellationToken)).Select(AgeKeySummary.Of).ToList();
    }

    /// <summary>
    /// Generates a new key pair. Files are encrypted for its recipient from now on; older keys keep decrypting
    /// the files not yet re-encrypted, until they are deleted.
    /// </summary>
    public async Task<AgeKeySummary> CreateAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await environments.GetAsync(environmentId, cancellationToken);

        var generated = await generator.GenerateAsync(cancellationToken);
        var key = AgeKey.Create(
            environmentId, generated.Recipient, protector.Protect(SecretPurpose.AgeIdentity, generated.Identity), time.GetUtcNow());
        keys.Add(key);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return AgeKeySummary.Of(key);
    }

    /// <summary>Forgets a key for good: files encrypted only for it cannot be decrypted any more.</summary>
    public async Task DeleteAsync(EnvironmentId environmentId, AgeKeyId keyId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);

        var key = await keys.FindAsync(keyId, cancellationToken);
        if (key is null || key.EnvironmentId != environmentId)
        {
            throw new NotFoundException("Age key", keyId);
        }

        keys.Remove(key);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
