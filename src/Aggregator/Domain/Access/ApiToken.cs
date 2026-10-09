using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A personal API token: the CLI and scripts act as its user with <c>Authorization: Bearer cf_pat_…</c>.
/// It has its user's roles, minus its own limits (<see cref="Scope"/>): read-only, some environments only, an expiry.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the token is stored (security invariant 8); the token itself is shown once, when it is created.
/// The token carries 256 random bits, so an unsalted hash is enough and lets a request find its token directly
/// (see <see cref="SecretToken"/>). <see cref="Hint"/> keeps its first characters to tell tokens apart in a list.
/// </remarks>
public sealed class ApiToken : Entity<ApiTokenId>
{
    /// <summary>Marks the token for secret scanners and tells it apart from other bearer secrets.</summary>
    public const string Prefix = "cf_pat_";

    public const int MaxNameLength = 64;

    /// <summary>How many characters of the token after <see cref="Prefix"/> the hint keeps.</summary>
    public const int HintLength = 6;

    /// <summary><see cref="LastUsedAt"/> is written at most this often, not on every request.</summary>
    public static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    private readonly List<ApiTokenEnvironment> environments = [];

    private ApiToken(ApiTokenId id, UserId userId, string name, string tokenHash, string hint, DateTimeOffset createdAt)
        : base(id)
    {
        UserId = userId;
        Name = name;
        TokenHash = tokenHash;
        Hint = hint;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private ApiToken()
    {
        Name = null!;
        TokenHash = null!;
        Hint = null!;
    }

    public UserId UserId { get; private init; }

    public string Name { get; private set; }

    public string TokenHash { get; private init; }

    /// <summary>The prefix and the first characters of the token, e.g. <c>cf_pat_x7Kq2m</c>.</summary>
    public string Hint { get; private init; }

    /// <summary>Only viewing: no deploys, no container actions, no changes of any kind.</summary>
    public bool IsReadOnly { get; private init; }

    /// <summary>Limited to <see cref="Environments"/>; such a token has no administrator rights.</summary>
    public bool IsRestricted { get; private init; }

    /// <summary>
    /// The environments a restricted token reaches. An environment removed later disappears from here; a restricted
    /// token whose environments are all gone reaches nothing, never everything.
    /// </summary>
    public IReadOnlyCollection<EnvironmentId> Environments => environments.Select(e => e.EnvironmentId).ToList();

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>Null for a token that does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; private init; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public AccessScope Scope => new(IsRestricted ? Environments : null, IsReadOnly);

    /// <summary>Creates a token and returns it with its secret, which is not kept anywhere.</summary>
    /// <param name="environmentIds">Null for every environment; otherwise at least one.</param>
    /// <param name="expiresAt">Null for a token that does not expire; otherwise in the future.</param>
    /// <exception cref="DomainValidationException">The name is empty or too long, the environment list is empty, or the expiry has passed.</exception>
    public static (ApiToken Token, string Secret) Create(
        UserId userId,
        string name,
        bool readOnly,
        IEnumerable<EnvironmentId>? environmentIds,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        var normalizedName = NormalizeName(name);
        if (expiresAt is { } expiry && expiry <= now)
        {
            throw new DomainValidationException("The expiry of a token must be in the future.");
        }

        var restrictedTo = environmentIds?.ToHashSet();
        if (restrictedTo is { Count: 0 })
        {
            throw new DomainValidationException("A token limited to environments needs at least one environment.");
        }

        var secret = Prefix + SecretToken.Generate();
        var token = new ApiToken(ApiTokenId.New(), userId, normalizedName, SecretToken.Hash(secret), secret[..(Prefix.Length + HintLength)], now)
        {
            IsReadOnly = readOnly,
            IsRestricted = restrictedTo is not null,
            ExpiresAt = expiresAt,
        };

        foreach (var environmentId in restrictedTo ?? [])
        {
            token.environments.Add(new ApiTokenEnvironment(environmentId));
        }

        return (token, secret);
    }

    /// <summary>True when the value has the shape of an API token, so it should be checked as one.</summary>
    public static bool HasTokenShape(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string HashSecret(string secret) => SecretToken.Hash(secret);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && now >= expiresAt;

    public void Rename(string name) => Name = NormalizeName(name);

    /// <summary>Records a request made with the token. Returns false when the previous record is recent enough to keep.</summary>
    public bool RecordUse(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < LastUsedPrecision)
        {
            return false;
        }

        LastUsedAt = now;
        return true;
    }

    private static string NormalizeName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned.Length > MaxNameLength)
        {
            throw new DomainValidationException($"Token name must be 1-{MaxNameLength} characters.");
        }

        return cleaned;
    }
}

/// <summary>One environment a restricted <see cref="ApiToken"/> reaches.</summary>
public sealed class ApiTokenEnvironment
{
    public ApiTokenEnvironment(EnvironmentId environmentId) => EnvironmentId = environmentId;

    public EnvironmentId EnvironmentId { get; private init; }
}
