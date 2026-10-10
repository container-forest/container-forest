using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A CLI asking for an API token through the browser (the device authorization flow of RFC 8628, ADR-0008). The CLI
/// holds the device code and polls with it; the person sees the user code in the CLI and approves the request in the
/// panel, choosing the token's limits. The token is created only when the CLI picks it up, so no token is stored here.
/// </summary>
/// <remarks>
/// Both codes are stored as hashes. The device code carries 256 random bits; the user code is short, so it is safe
/// only together with the short lifetime, a single approval and the rate limit of the endpoints that take it.
/// </remarks>
public sealed class CliAuthorization : Entity<CliAuthorizationId>
{
    public const int UserCodeLength = 8;
    public const int MaxClientNameLength = 64;
    public const int MaxHostNameLength = 128;

    /// <summary>Long enough for an address with an IPv6 zone.</summary>
    public const int MaxAddressLength = 64;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How often the CLI may poll; a faster poll is told to slow down.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly List<CliAuthorizationEnvironment> tokenEnvironments = [];

    private CliAuthorization(
        CliAuthorizationId id,
        string deviceCodeHash,
        string userCodeHash,
        string clientName,
        string hostName,
        string? requestedFrom,
        bool secretsRequested,
        DateTimeOffset now)
        : base(id)
    {
        SecretsRequested = secretsRequested;
        DeviceCodeHash = deviceCodeHash;
        UserCodeHash = userCodeHash;
        ClientName = clientName;
        HostName = hostName;
        RequestedFrom = requestedFrom;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
        Status = CliAuthorizationStatus.Pending;
    }

    // For EF Core.
    private CliAuthorization()
    {
        DeviceCodeHash = null!;
        UserCodeHash = null!;
        ClientName = null!;
        HostName = null!;
    }

    public string DeviceCodeHash { get; private init; }

    public string UserCodeHash { get; private init; }

    /// <summary>What asks, as the CLI calls itself, e.g. "forest 0.1.0". Shown to the person; never trusted.</summary>
    public string ClientName { get; private init; }

    /// <summary>The machine the CLI runs on, as it says. Shown to the person; never trusted.</summary>
    public string HostName { get; private init; }

    /// <summary>The address the request came from, as the aggregator saw it; null when unknown.</summary>
    public string? RequestedFrom { get; private init; }

    /// <summary>
    /// The CLI asks for a token that may decrypt secrets (<c>forest login --secrets</c>). Only a suggestion: the person
    /// decides on the approval page.
    /// </summary>
    public bool SecretsRequested { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset ExpiresAt { get; private init; }

    public DateTimeOffset? LastPolledAt { get; private set; }

    public CliAuthorizationStatus Status { get; private set; }

    /// <summary>The user who approved; the token is theirs.</summary>
    public UserId? ApprovedBy { get; private set; }

    public string? TokenName { get; private set; }

    public bool TokenReadOnly { get; private set; }

    public bool TokenRestricted { get; private set; }

    public bool TokenCanDecryptSecrets { get; private set; }

    public IReadOnlyCollection<EnvironmentId> TokenEnvironments => tokenEnvironments.Select(e => e.EnvironmentId).ToList();

    public DateTimeOffset? TokenExpiresAt { get; private set; }

    /// <summary>Starts a request. Returns it with the device code for the CLI and the user code for the person.</summary>
    /// <exception cref="DomainValidationException">The client or host name is empty or too long.</exception>
    /// <param name="secretsRequested">The CLI asks for a token that may decrypt secrets; the person decides.</param>
    public static (CliAuthorization Authorization, string DeviceCode, string UserCode) Start(
        string clientName, string hostName, string? requestedFrom, DateTimeOffset now, bool secretsRequested = false)
    {
        var deviceCode = SecretToken.Generate();
        var userCode = OneTimeCode.Generate(UserCodeLength);
        var authorization = new CliAuthorization(
            CliAuthorizationId.New(),
            SecretToken.Hash(deviceCode),
            OneTimeCode.Hash(userCode),
            Clean(clientName, MaxClientNameLength, "Client name"),
            Clean(hostName, MaxHostNameLength, "Host name"),
            requestedFrom is null ? null : Truncate(requestedFrom, MaxAddressLength),
            secretsRequested,
            now);
        return (authorization, deviceCode, userCode);
    }

    /// <summary>The user code as it is shown: two groups of four, e.g. "WDJB-MJHT".</summary>
    public static string FormatUserCode(string userCode)
    {
        var normalized = OneTimeCode.Normalize(userCode);
        return normalized.Length == UserCodeLength ? $"{normalized[..4]}-{normalized[4..]}" : normalized;
    }

    public static string HashUserCode(string userCode) => OneTimeCode.Hash(userCode);

    public static string HashDeviceCode(string deviceCode) => SecretToken.Hash(deviceCode);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>A request the person can still approve or deny.</summary>
    public bool IsOpen(DateTimeOffset now) => Status == CliAuthorizationStatus.Pending && !IsExpired(now);

    /// <summary>The person approves: the token will be theirs, with these limits.</summary>
    /// <param name="environmentIds">Null for every environment.</param>
    /// <param name="canDecryptSecrets">The token may have the aggregator decrypt SOPS files (ADR-0010).</param>
    /// <exception cref="DomainConflictException">The request was already decided or has expired.</exception>
    /// <exception cref="DomainValidationException">The token's options are invalid.</exception>
    public void Approve(
        UserId userId,
        string tokenName,
        bool readOnly,
        IReadOnlyCollection<EnvironmentId>? environmentIds,
        DateTimeOffset? tokenExpiresAt,
        DateTimeOffset now,
        bool canDecryptSecrets = false)
    {
        EnsureOpen(now);
        TokenName = ApiToken.ValidateOptions(tokenName, readOnly, canDecryptSecrets, environmentIds, tokenExpiresAt, now);
        TokenReadOnly = readOnly;
        TokenCanDecryptSecrets = canDecryptSecrets;
        TokenRestricted = environmentIds is not null;
        foreach (var environmentId in environmentIds?.Distinct() ?? [])
        {
            tokenEnvironments.Add(new CliAuthorizationEnvironment(environmentId));
        }

        TokenExpiresAt = tokenExpiresAt;
        ApprovedBy = userId;
        Status = CliAuthorizationStatus.Approved;
    }

    /// <exception cref="DomainConflictException">The request was already decided or has expired.</exception>
    public void Deny(DateTimeOffset now)
    {
        EnsureOpen(now);
        Status = CliAuthorizationStatus.Denied;
    }

    /// <summary>
    /// The CLI asks how its request stands. Polling faster than <see cref="PollInterval"/> is answered with
    /// <see cref="CliPollResult.SlowDown"/> and does not count as a poll.
    /// </summary>
    public CliPollResult Poll(DateTimeOffset now)
    {
        if (IsExpired(now))
        {
            return CliPollResult.Expired;
        }

        if (LastPolledAt is { } last && now - last < PollInterval && Status == CliAuthorizationStatus.Pending)
        {
            return CliPollResult.SlowDown;
        }

        LastPolledAt = now;
        return Status switch
        {
            CliAuthorizationStatus.Approved => CliPollResult.Approved,
            CliAuthorizationStatus.Denied => CliPollResult.Denied,
            _ => CliPollResult.Pending,
        };
    }

    /// <summary>Creates the approved token and returns it with its secret; the request is used up after this.</summary>
    /// <exception cref="DomainConflictException">The request is not approved.</exception>
    public (ApiToken Token, string Secret) IssueToken(DateTimeOffset now)
    {
        if (Status != CliAuthorizationStatus.Approved || ApprovedBy is not { } userId || TokenName is null)
        {
            throw new DomainConflictException("The CLI login has not been approved.");
        }

        return ApiToken.Create(
            userId, TokenName, TokenReadOnly, TokenRestricted ? TokenEnvironments : null, TokenExpiresAt, now, TokenCanDecryptSecrets);
    }

    private void EnsureOpen(DateTimeOffset now)
    {
        if (!IsOpen(now))
        {
            throw new DomainConflictException("This CLI login was already approved or denied, or it has expired; start it again.");
        }
    }

    private static string Clean(string value, int maxLength, string what)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            throw new DomainValidationException($"{what} must not be empty.");
        }

        return Truncate(cleaned, maxLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;
}

public enum CliAuthorizationStatus
{
    Pending,
    Approved,
    Denied,
}

/// <summary>What the CLI is told when it polls.</summary>
public enum CliPollResult
{
    Pending,

    /// <summary>The CLI polled too soon; it should wait longer between polls.</summary>
    SlowDown,
    Approved,
    Denied,
    Expired,
}

/// <summary>One environment the token of an approved <see cref="CliAuthorization"/> will reach.</summary>
public sealed class CliAuthorizationEnvironment
{
    public CliAuthorizationEnvironment(EnvironmentId environmentId) => EnvironmentId = environmentId;

    public EnvironmentId EnvironmentId { get; private init; }
}
