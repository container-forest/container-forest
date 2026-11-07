using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A user's public SSH key that signs commits (ADR-0011). A commit signed with it is endorsed by its user: the
/// aggregator deploys it to a stack that requires signed commits when the user may deploy that stack, within the
/// key's own limits (<see cref="Scope"/>).
/// </summary>
/// <remarks>
/// Only the public key is kept. The fingerprint identifies the key: one key cannot belong to two users, or a
/// signature would not say who endorsed the commit.
/// </remarks>
public sealed class SigningKey : Entity<SigningKeyId>
{
    public const int MaxNameLength = 64;

    /// <summary>Longest public key accepted, in the OpenSSH text form; an RSA key of 16384 bits fits.</summary>
    public const int MaxPublicKeyLength = 4096;

    /// <summary><see cref="LastUsedAt"/> is written at most this often.</summary>
    public static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    private readonly List<SigningKeyEnvironment> environments = [];

    private SigningKey(SigningKeyId id, UserId userId, string name, SshPublicKey publicKey, UserId addedBy, DateTimeOffset createdAt)
        : base(id)
    {
        UserId = userId;
        Name = name;
        PublicKey = publicKey.OpenSsh;
        Algorithm = publicKey.Algorithm;
        Fingerprint = publicKey.Fingerprint;
        AddedBy = addedBy;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private SigningKey()
    {
        Name = null!;
        PublicKey = null!;
        Algorithm = null!;
        Fingerprint = null!;
    }

    public UserId UserId { get; private init; }

    public string Name { get; private set; }

    /// <summary>"type base64", without the comment.</summary>
    public string PublicKey { get; private init; }

    public string Algorithm { get; private init; }

    /// <summary>"SHA256:…"; unique among all keys.</summary>
    public string Fingerprint { get; private init; }

    /// <summary>
    /// Who added the key: the user, or an administrator who added it for them (a service account without a session).
    /// The panel shows it next to signatures, so an administrator cannot quietly sign in someone else's name.
    /// </summary>
    public UserId AddedBy { get; private init; }

    public bool AddedByAdministrator => AddedBy != UserId;

    /// <summary>Endorses deployments only in <see cref="Environments"/>.</summary>
    public bool IsRestricted { get; private set; }

    /// <summary>
    /// The environments a restricted key endorses. An environment removed later disappears from here; a restricted
    /// key whose environments are all gone endorses nothing, never everything.
    /// </summary>
    public IReadOnlyCollection<EnvironmentId> Environments => environments.Select(e => e.EnvironmentId).ToList();

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>When the key last endorsed a deployment; null when never.</summary>
    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsHardwareBacked => Algorithm is SshPublicKey.SkEd25519 or SshPublicKey.SkEcdsaP256;

    /// <summary>
    /// What a commit signed with the key may reach on top of its user's roles: every environment, or the key's own.
    /// Like the limits of an API token, it only takes rights away.
    /// </summary>
    public AccessScope Scope => new(IsRestricted ? Environments : null, readOnly: false);

    public SshPublicKey ParsedKey => SshPublicKey.Parse(PublicKey);

    /// <param name="environmentIds">Null for every environment; otherwise at least one.</param>
    /// <param name="addedBy">The user themselves, or the administrator adding the key for them.</param>
    /// <exception cref="DomainValidationException">The name is empty or too long, the key is not an accepted public SSH key, or the environment list is empty.</exception>
    public static SigningKey Create(
        UserId userId,
        string name,
        string publicKey,
        IEnumerable<EnvironmentId>? environmentIds,
        UserId addedBy,
        DateTimeOffset now)
    {
        if (publicKey.Length > MaxPublicKeyLength)
        {
            throw new DomainValidationException("The public SSH key is too long.");
        }

        var key = new SigningKey(SigningKeyId.New(), userId, NormalizeName(name), SshPublicKey.Parse(publicKey), addedBy, now);
        key.SetEnvironments(environmentIds);
        return key;
    }

    /// <summary>Changes which environments the key endorses; null for all of them.</summary>
    /// <exception cref="DomainValidationException">The environment list is empty.</exception>
    public void ChangeScope(IEnumerable<EnvironmentId>? environmentIds) => SetEnvironments(environmentIds);

    public void Rename(string name) => Name = NormalizeName(name);

    /// <summary>True when the key now reaches an environment it did not reach before.</summary>
    public bool WouldWiden(IEnumerable<EnvironmentId>? environmentIds) =>
        IsRestricted && (environmentIds is null || environmentIds.Any(e => !Environments.Contains(e)));

    /// <summary>Records that the key endorsed a deployment. Returns false when the previous record is recent enough to keep.</summary>
    public bool RecordUse(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < LastUsedPrecision)
        {
            return false;
        }

        LastUsedAt = now;
        return true;
    }

    private void SetEnvironments(IEnumerable<EnvironmentId>? environmentIds)
    {
        var restrictedTo = environmentIds?.ToHashSet();
        if (restrictedTo is { Count: 0 })
        {
            throw new DomainValidationException("A signing key limited to environments needs at least one environment.");
        }

        IsRestricted = restrictedTo is not null;
        environments.RemoveAll(e => restrictedTo?.Contains(e.EnvironmentId) != true);
        foreach (var environmentId in restrictedTo ?? [])
        {
            if (environments.All(e => e.EnvironmentId != environmentId))
            {
                environments.Add(new SigningKeyEnvironment(environmentId));
            }
        }
    }

    private static string NormalizeName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned.Length > MaxNameLength)
        {
            throw new DomainValidationException($"Signing key name must be 1-{MaxNameLength} characters.");
        }

        return cleaned;
    }
}

/// <summary>One environment a restricted <see cref="SigningKey"/> endorses deployments in.</summary>
public sealed class SigningKeyEnvironment
{
    public SigningKeyEnvironment(EnvironmentId environmentId) => EnvironmentId = environmentId;

    public EnvironmentId EnvironmentId { get; private init; }
}
