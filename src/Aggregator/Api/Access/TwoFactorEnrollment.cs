using ContainerForest.Aggregator.Application.Access;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// When the system requires two-factor of administrators (ADR-0019), a session of an administrator with neither an
/// authenticator app nor a passkey can only set one up: every other request is 403 with code
/// <c>TwoFactorSetupRequired</c>. Only sessions: an API token is a credential of its own, and the bot acts for the user
/// through its own token.
/// </summary>
internal static class TwoFactorEnrollment
{
    public const string ProblemCode = "TwoFactorSetupRequired";

    /// <summary>What such a session may still reach: who it is, signing out, confirming, two-factor and passkeys.</summary>
    private static readonly string[] Allowed = ["/api/me/two-factor", "/api/me/passkeys", "/api/auth/"];

    public static IApplicationBuilder UseTwoFactorEnrollment(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: var type }
            && type == IdentityConstants.ApplicationScheme
            && path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && path != "/api/me"
            && !Allowed.Any(prefix => path.StartsWithSegments(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            var services = context.RequestServices;
            var actor = await services.GetRequiredService<CurrentActor>().GetAsync(context.RequestAborted);
            if (await services.GetRequiredService<TwoFactorManagement>().RequiresSetupAsync(actor.User, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Two-factor authentication required",
                        detail: "Administrators must set up an authenticator app or a passkey before doing anything else.",
                        extensions: new Dictionary<string, object?> { ["code"] = ProblemCode })
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    });
}
