using System.Diagnostics.CodeAnalysis;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Environments;

/// <summary>
/// A Docker host managed by one agent. The environment id doubles as the agent id,
/// so the environment of a connection always follows from the agent's credentials.
/// </summary>
/// <remarks>Named DockerEnvironment to avoid clashing with <see cref="System.Environment"/>.</remarks>
public sealed class DockerEnvironment : Entity<EnvironmentId>
{
    public const int MaxNameLength = 64;

    public static readonly TimeSpan DefaultEnrollmentTokenLifetime = TimeSpan.FromHours(24);

    private DockerEnvironment(EnvironmentId id, string name, bool isPrivate, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        IsPrivate = isPrivate;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private DockerEnvironment()
    {
        Name = null!;
    }

    public string Name { get; private set; }

    /// <summary>A private environment is visible only to users with a role on it or on one of its stacks.</summary>
    public bool IsPrivate { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public string? EnrollmentTokenHash { get; private set; }

    public DateTimeOffset? EnrollmentTokenExpiresAt { get; private set; }

    public string? AgentSecretHash { get; private set; }

    public DateTimeOffset? EnrolledAt { get; private set; }

    public string? AgentVersion { get; private set; }

    /// <summary>Host boot id reported in the last Hello; a new value means the host rebooted.</summary>
    public string? LastBootId { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public bool IsEnrolled => AgentSecretHash is not null;

    public static DockerEnvironment Create(string name, bool isPrivate, DateTimeOffset now) =>
        new(EnvironmentId.New(), NormalizeName(name), isPrivate, now);

    public void Rename(string name) => Name = NormalizeName(name);

    public void MakePrivate() => IsPrivate = true;

    public void MakePublic() => IsPrivate = false;

    /// <summary>
    /// Issues a one-time enrollment token and returns it in plain text; only its hash is kept.
    /// A previously issued, unused token stops working.
    /// </summary>
    public string IssueEnrollmentToken(DateTimeOffset now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainValidationException("Enrollment token lifetime must be positive.");
        }

        var token = SecretToken.Generate();
        EnrollmentTokenHash = SecretToken.Hash(token);
        EnrollmentTokenExpiresAt = now + lifetime;
        return token;
    }

    /// <summary>
    /// Consumes a valid enrollment token and returns a new agent secret in plain text.
    /// Enrolling again replaces the previous secret, so an old agent can no longer connect.
    /// </summary>
    public bool TryEnroll(string enrollmentToken, DateTimeOffset now, [NotNullWhen(true)] out string? agentSecret)
    {
        agentSecret = null;

        if (EnrollmentTokenHash is null || EnrollmentTokenExpiresAt is not { } expiresAt || now >= expiresAt)
        {
            return false;
        }

        if (!SecretToken.Matches(enrollmentToken, EnrollmentTokenHash))
        {
            return false;
        }

        EnrollmentTokenHash = null;
        EnrollmentTokenExpiresAt = null;

        agentSecret = SecretToken.Generate();
        AgentSecretHash = SecretToken.Hash(agentSecret);
        EnrolledAt = now;
        AgentVersion = null;
        LastBootId = null;
        return true;
    }

    public bool VerifyAgentSecret(string agentSecret) =>
        AgentSecretHash is not null && SecretToken.Matches(agentSecret, AgentSecretHash);

    /// <summary>
    /// Records an agent connection (its Hello message).
    /// Returns true when the host rebooted since the previous connection, so file secrets must be re-sent.
    /// </summary>
    public bool RecordConnection(string bootId, string agentVersion, DateTimeOffset now)
    {
        if (!IsEnrolled)
        {
            throw new DomainConflictException($"Environment '{Name}' has no enrolled agent.");
        }

        if (string.IsNullOrWhiteSpace(bootId))
        {
            throw new DomainValidationException("Agent reported an empty boot id.");
        }

        var rebooted = LastBootId is not null && LastBootId != bootId;
        LastBootId = bootId;
        AgentVersion = agentVersion;
        TouchLastSeen(now);
        return rebooted;
    }

    public void RecordHeartbeat(DateTimeOffset now) => TouchLastSeen(now);

    /// <summary>Revokes the agent's credentials; the agent must be enrolled again with a new token.</summary>
    public void RevokeAgent()
    {
        AgentSecretHash = null;
        EnrolledAt = null;
        AgentVersion = null;
        LastBootId = null;
    }

    private void TouchLastSeen(DateTimeOffset now)
    {
        // Messages can be processed out of order; never move the timestamp back.
        if (LastSeenAt is null || now > LastSeenAt)
        {
            LastSeenAt = now;
        }
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException(
                $"Environment name must be 1-{MaxNameLength} characters without control characters.");
        }

        return trimmed;
    }
}
