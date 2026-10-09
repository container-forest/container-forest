using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Application.Access;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Signing in with Discord and linking a Discord account through Discord's OAuth2 (ADR-0009). These are browser
/// navigations, not fetches: each answers with a redirect or a page.
/// </summary>
//   GET /api/auth/discord/login?redirect=/path -> 302 to Discord; 404 when Discord sign-in is off
//   GET /api/me/discord/oauth                  -> 302 to Discord to link the signed-in user; to /login without a session
//   GET /api/auth/discord/callback             -> the OAuth handler (Discord sends the browser here)
//   GET /api/auth/discord/complete             -> a page that goes on to the redirect path, /login?error=discord,
//                                                 /discord?linked=discord or /discord?error=discord
internal static class DiscordAuthEndpoints
{
    public static IEndpointRouteBuilder MapDiscordAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithTags("Account").ExcludeFromDescription();

        group.MapGet("/api/auth/discord/login", (string? redirect, DiscordAuthOptions options) =>
            options.IsConfigured
                ? Results.Challenge(DiscordSignIn.LoginProperties(redirect), [DiscordSignIn.Scheme])
                : Results.NotFound());

        group.MapGet("/api/me/discord/oauth", async (DiscordAuthOptions options, DiscordSignIn discord, CancellationToken ct) =>
        {
            if (!options.IsConfigured)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Challenge(await discord.LinkPropertiesAsync(ct), [DiscordSignIn.Scheme]);
            }
            catch (UnauthenticatedException)
            {
                return Results.Redirect("/login?redirect=%2Fdiscord");
            }
        });

        group.MapGet(DiscordSignIn.CompletePath, async (HttpContext http, DiscordAuthOptions options, DiscordSignIn discord, CancellationToken ct) =>
            options.IsConfigured
                ? DiscordSignIn.ContinuePage(http, await discord.CompleteAsync(http, ct))
                : Results.NotFound());

        return app;
    }
}
