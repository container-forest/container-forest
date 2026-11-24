using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// How a session was signed in, kept in its <see cref="ClaimTypes.AuthenticationMethod"/> claim (Identity's
/// <c>amr</c>): the requirement for administrators (ADR-0019) depends on it, not only on what the account has.
/// </summary>
internal static class SignInMethods
{
    /// <summary>A password alone: an account without an authenticator app.</summary>
    public const string Password = "pwd";

    /// <summary>A password or Discord, then the code of the app or a recovery code.</summary>
    public const string TwoFactor = "mfa";

    public const string Passkey = "passkey";

    public const string Discord = DiscordSignIn.Scheme;

    /// <summary>The method of a session cookie; null for anything else (an API token, the dev header, the bot) and for a session from before the claim was kept.</summary>
    public static string? Of(ClaimsPrincipal principal) =>
        principal.Identity is { IsAuthenticated: true, AuthenticationType: var type } && type == IdentityConstants.ApplicationScheme
            ? principal.FindFirstValue(ClaimTypes.AuthenticationMethod) ?? Unknown
            : null;

    /// <summary>A session cookie whose claim is missing, e.g. one issued before the claim was kept.</summary>
    public const string Unknown = "unknown";
}
