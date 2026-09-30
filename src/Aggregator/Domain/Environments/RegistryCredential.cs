using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Domain.Environments;

/// <summary>
/// Login to a container registry that the agent of one environment uses to pull images. Credentials belong to an
/// environment, not to the whole system: an Editor of one environment, who is root on its host, can read what that
/// agent receives, so other environments' credentials never reach it.
/// </summary>
/// <remarks>
/// The password is stored already encrypted by the infrastructure layer; the domain never sees it in plain text.
/// One credential per registry host and environment, as in a docker config.json.
/// </remarks>
public sealed class RegistryCredential : Entity<RegistryCredentialId>
{
    public const int MaxUsernameLength = 255;

    private RegistryCredential(
        RegistryCredentialId id, EnvironmentId environmentId, string server, string username, string protectedPassword, DateTimeOffset now)
        : base(id)
    {
        EnvironmentId = environmentId;
        Server = server;
        Username = username;
        ProtectedPassword = protectedPassword;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // For EF Core.
    private RegistryCredential()
    {
        Server = null!;
        Username = null!;
        ProtectedPassword = null!;
    }

    public EnvironmentId EnvironmentId { get; private init; }

    /// <summary>Normalized registry host, e.g. "ghcr.io" or <see cref="RegistryHost.DockerHub"/>.</summary>
    public string Server { get; private init; }

    public string Username { get; private set; }

    /// <summary>Encrypted password or access token.</summary>
    public string ProtectedPassword { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <param name="server">Registry host in any form <see cref="RegistryHost.TryParse"/> accepts.</param>
    /// <exception cref="DomainValidationException">The server, user name or password is not usable.</exception>
    public static RegistryCredential Create(
        EnvironmentId environmentId, string server, string username, string protectedPassword, DateTimeOffset now) =>
        new(RegistryCredentialId.New(), environmentId, ParseServer(server), ValidateUsername(username), ValidatePassword(protectedPassword), now);

    /// <summary>Normalizes a server name the way <see cref="Create"/> stores it, e.g. to look up an existing credential.</summary>
    /// <exception cref="DomainValidationException">The value is not a registry host.</exception>
    public static string ParseServer(string server) =>
        RegistryHost.TryParse(server, out var host)
            ? host
            : throw new DomainValidationException(
                $"'{server}' is not a registry host; use a host name with an optional port, e.g. ghcr.io or registry.example.com:5000.");

    /// <summary>Replaces the login, e.g. after the token was rotated.</summary>
    public void ChangeLogin(string username, string protectedPassword, DateTimeOffset now)
    {
        Username = ValidateUsername(username);
        ProtectedPassword = ValidatePassword(protectedPassword);
        UpdatedAt = now;
    }

    /// <summary>Whether an agent needs this credential to pull <paramref name="image"/>.</summary>
    public bool Serves(ImageReference image) => RegistryHost.Of(image) == Server;

    private static string ValidateUsername(string username)
    {
        var trimmed = username.Trim();
        // docker config.json stores "user:password" and splits it at the first ':'.
        if (trimmed.Length is 0 or > MaxUsernameLength || trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException(
                $"Registry user name must be 1-{MaxUsernameLength} characters without ':' or control characters.");
        }

        return trimmed;
    }

    private static string ValidatePassword(string protectedPassword) =>
        string.IsNullOrEmpty(protectedPassword)
            ? throw new DomainValidationException("Registry password must not be empty.")
            : protectedPassword;
}
