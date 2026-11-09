using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface ISigningKeyRepository : IRepository<SigningKey, SigningKeyId>
{
    Task<SigningKey?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken);

    /// <summary>The user's keys, newest first.</summary>
    Task<IReadOnlyList<SigningKey>> ListForUserAsync(UserId userId, CancellationToken cancellationToken);

    Task<int> CountForUserAsync(UserId userId, CancellationToken cancellationToken);
}

/// <summary>A signing key with the name of the administrator who added it for its user; null when the user did.</summary>
public sealed record SigningKeySummary(SigningKey Key, string? AddedByAdministrator);

/// <summary>
/// Public SSH keys that endorse commits (ADR-0011). A user manages their own keys only from a session, never with an
/// API token: a read-only token that could add a key would gain deploys through git. Administrators list and remove
/// anyone's keys and add keys for users without a session, such as a CI account.
/// </summary>
public sealed class SigningKeyService(
    CurrentActor currentActor,
    ISigningKeyRepository keys,
    Images.IAggregatorSigningKeyRepository aggregatorKeys,
    IUserRepository users,
    ApiTokenService tokens,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    public const int MaxKeysPerUser = 20;

    public async Task<IReadOnlyList<SigningKeySummary>> ListMineAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        return await SummarizeAsync(await keys.ListForUserAsync(user.Id, cancellationToken), cancellationToken);
    }

    /// <param name="environmentIds">Null for every environment; otherwise environments the user can see.</param>
    /// <exception cref="DomainValidationException">Not an accepted public SSH key, or an invalid name.</exception>
    /// <exception cref="DomainConflictException">The key is already added, by anyone, or the user has <see cref="MaxKeysPerUser"/> keys.</exception>
    public Task<SigningKeySummary> AddMineAsync(
        string name, string publicKey, IReadOnlyCollection<EnvironmentId>? environmentIds, CancellationToken cancellationToken) =>
        AddAsync(null, name, publicKey, environmentIds, cancellationToken);

    /// <summary>Changes the name and the environments a key endorses; a session only.</summary>
    /// <exception cref="NotFoundException">The user has no such key, or an environment is not visible to them.</exception>
    public Task<SigningKeySummary> UpdateMineAsync(
        SigningKeyId id, string name, IReadOnlyCollection<EnvironmentId>? environmentIds, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SigningKeyChanged) { Details = ScopeOf(environmentIds) };
        return audit.RunAsync(draft, async () =>
        {
            var user = await currentActor.GetSignedInUserAsync(cancellationToken);
            var key = await OwnKeyAsync(user.Id, id, cancellationToken);
            draft.Target = TargetOf(key);
            await tokens.EnsureEnvironmentsVisibleAsync(user, environmentIds, cancellationToken);
            if (key.WouldWiden(environmentIds))
            {
                draft.Details = $"{ScopeOf(environmentIds)}; widened";
            }

            key.Rename(name);
            key.ChangeScope(environmentIds);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return (await SummarizeAsync([key], cancellationToken))[0];
        }, cancellationToken);
    }

    /// <exception cref="NotFoundException">The user has no such key.</exception>
    public async Task RemoveMineAsync(SigningKeyId id, CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        await RemoveAsync(user.Id, id, cancellationToken);
    }

    /// <exception cref="NotFoundException">The user does not exist.</exception>
    public async Task<IReadOnlyList<SigningKeySummary>> ListForUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var user = await users.GetAsync(userId, cancellationToken);
        return await SummarizeAsync(await keys.ListForUserAsync(user.Id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// An administrator adds a key for another user, e.g. a CI account that never signs in. The key records who added
    /// it, and the panel shows that next to its signatures.
    /// </summary>
    /// <exception cref="NotFoundException">The user does not exist, or an environment does not.</exception>
    public Task<SigningKeySummary> AddForUserAsync(
        UserId userId, string name, string publicKey, IReadOnlyCollection<EnvironmentId>? environmentIds, CancellationToken cancellationToken) =>
        AddAsync(userId, name, publicKey, environmentIds, cancellationToken);

    /// <exception cref="NotFoundException">The user has no such key.</exception>
    public async Task RemoveForUserAsync(UserId userId, SigningKeyId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await RemoveAsync(userId, id, cancellationToken);
    }

    /// <param name="ownerId">Null for the current user's own key; otherwise an administrator adds it for that user.</param>
    private Task<SigningKeySummary> AddAsync(
        UserId? ownerId,
        string name,
        string publicKey,
        IReadOnlyCollection<EnvironmentId>? environmentIds,
        CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SigningKeyAdded, new SigningKeyAuditTarget(null, name.Trim()))
        {
            Details = ScopeOf(environmentIds),
        };
        return audit.RunAsync(draft, async () =>
        {
            User owner;
            User addedBy;
            if (ownerId is { } id)
            {
                addedBy = (await currentActor.GetAdministratorAsync(cancellationToken)).User;
                owner = await users.GetAsync(id, cancellationToken);
                draft.Details = $"{ScopeOf(environmentIds)}; for {owner.UserName}";
            }
            else
            {
                owner = addedBy = await currentActor.GetSignedInUserAsync(cancellationToken);
            }

            await tokens.EnsureEnvironmentsVisibleAsync(owner, environmentIds, cancellationToken);
            if (await keys.CountForUserAsync(owner.Id, cancellationToken) >= MaxKeysPerUser)
            {
                throw new DomainConflictException($"A user can have at most {MaxKeysPerUser} signing keys; remove unused ones first.");
            }

            var key = SigningKey.Create(owner.Id, name, publicKey, environmentIds, addedBy.Id, time.GetUtcNow());
            draft.Target = TargetOf(key);
            if (await keys.FindByFingerprintAsync(key.Fingerprint, cancellationToken) is not null)
            {
                // Said the same way whoever has it: whose key it is does not leak.
                throw new DomainConflictException($"The key {key.Fingerprint} is already added.");
            }

            // The aggregator's own key signs update commits but never endorses them (ADR-0015): trusting it must not be
            // possible even by accident.
            if (await aggregatorKeys.ExistsAsync(key.Fingerprint, cancellationToken))
            {
                throw new DomainValidationException(
                    $"The key {key.Fingerprint} is the aggregator's own signing key; it cannot endorse commits.");
            }

            keys.Add(key);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new SigningKeySummary(key, ownerId is null ? null : addedBy.UserName);
        }, cancellationToken);
    }

    private async Task RemoveAsync(UserId owner, SigningKeyId id, CancellationToken cancellationToken)
    {
        var key = await OwnKeyAsync(owner, id, cancellationToken);
        var keyOwner = await users.FindAsync(owner, cancellationToken);
        var draft = new AuditDraft(AuditAction.SigningKeyRemoved, TargetOf(key))
        {
            Details = keyOwner is null ? null : $"Owner: {keyOwner.UserName}",
        };
        await audit.RunAsync(draft, async () =>
        {
            keys.Remove(key);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task<SigningKey> OwnKeyAsync(UserId owner, SigningKeyId id, CancellationToken cancellationToken) =>
        // Someone else's key is as unknown as a missing one.
        await keys.FindAsync(id, cancellationToken) is { } key && key.UserId == owner
            ? key
            : throw new NotFoundException(nameof(SigningKey), id);

    private async Task<IReadOnlyList<SigningKeySummary>> SummarizeAsync(
        IReadOnlyList<SigningKey> list, CancellationToken cancellationToken)
    {
        var summaries = new List<SigningKeySummary>(list.Count);
        foreach (var key in list)
        {
            string? addedBy = null;
            if (key.AddedByAdministrator)
            {
                addedBy = (await users.FindAsync(key.AddedBy, cancellationToken))?.UserName ?? "a removed administrator";
            }

            summaries.Add(new SigningKeySummary(key, addedBy));
        }

        return summaries;
    }

    private static SigningKeyAuditTarget TargetOf(SigningKey key) => new(key.Id, $"{key.Name} ({key.Fingerprint})");

    private static string ScopeOf(IReadOnlyCollection<EnvironmentId>? environmentIds) =>
        environmentIds is null ? "all environments" : $"{environmentIds.Count} environment(s)";
}
