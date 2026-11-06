using System.Globalization;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Settings;

/// <summary>The action needs a fresh proof of who is acting, and the request has none. Maps to HTTP 403.</summary>
public sealed class IdentityConfirmationRequiredException()
    : Exception("Confirm your identity with your password or a passkey and try again.");

/// <param name="Token">Opaque, for the action that needs it; valid only for this user, until <paramref name="ExpiresAt"/>.</param>
public sealed record IssuedConfirmation(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Short-lived proof that the person at the session just gave their password or used a passkey (ADR-0013), for actions
/// a stolen session must not be able to do alone. Stateless: the token is protected with Data Protection.
/// </summary>
public sealed class IdentityConfirmations(ISecretProtector protector, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public IssuedConfirmation Issue(UserId userId)
    {
        var expiresAt = time.GetUtcNow() + Lifetime;
        var payload = string.Create(CultureInfo.InvariantCulture, $"{userId}|{expiresAt.ToUnixTimeSeconds()}");
        return new IssuedConfirmation(protector.Protect(SecretPurpose.IdentityConfirmation, payload), expiresAt);
    }

    /// <exception cref="IdentityConfirmationRequiredException">The token is missing, damaged, expired or someone else's.</exception>
    public void Require(string? token, UserId userId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new IdentityConfirmationRequiredException();
        }

        string payload;
        try
        {
            payload = protector.Unprotect(SecretPurpose.IdentityConfirmation, token);
        }
        catch (SecretUnreadableException)
        {
            throw new IdentityConfirmationRequiredException();
        }

        var parts = payload.Split('|');
        if (parts.Length != 2
            || parts[0] != userId.ToString()
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || time.GetUtcNow().ToUnixTimeSeconds() >= expires)
        {
            throw new IdentityConfirmationRequiredException();
        }
    }
}
