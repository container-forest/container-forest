using System.Security.Claims;
using System.Text.Encodings.Web;
using AspNet.Security.OAuth.Discord;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>Discord's OAuth2 application of the aggregator; without both values Discord sign-in is off.</summary>
internal sealed class DiscordAuthOptions
{
    public const string SectionName = "DiscordAuth";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Signing in with Discord and linking a Discord account through Discord's OAuth2 (ADR-0009). The identity is the
/// <c>DiscordLink</c> the bot uses, not an external login of Identity: one link for the bot and for signing in.
/// </summary>
/// <remarks>
/// Discord sends the browser back from another site, and the session cookie is SameSite=Strict: a cookie set in that
/// chain of redirects is not sent on its next step. So the last step answers with a page that navigates on from our
/// own site (<see cref="ContinuePage"/>). Linking cannot rely on the session either: the user who started it travels,
/// protected by Data Protection, in the OAuth state.
/// </remarks>
internal sealed class DiscordSignIn(SignInManager<AccountUser> signInManager, DiscordLinkService links, CurrentActor currentActor, ILogger<DiscordSignIn> logger)
{
    public const string Scheme = DiscordAuthenticationDefaults.AuthenticationScheme;
    public const string CallbackPath = "/api/auth/discord/callback";
    public const string CompletePath = "/api/auth/discord/complete";

    private const string PurposeKey = "cf.purpose";
    private const string RedirectKey = "cf.redirect";
    private const string UserKey = "cf.user";
    private const string Login = "login";
    private const string Link = "link";

    public const string LoginFailedPath = "/login?error=discord";
    public const string LinkedPath = "/discord?linked=discord";
    public const string LinkFailedPath = "/discord?error=discord";

    /// <summary>Registers the Discord scheme and the cookies of the round trip, when Discord sign-in is configured.</summary>
    public static void AddTo(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(DiscordAuthOptions.SectionName).Get<DiscordAuthOptions>() ?? new DiscordAuthOptions();
        services.AddSingleton(options);
        services.AddScoped<DiscordSignIn>();
        if (!options.IsConfigured)
        {
            return;
        }

        var secure = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        services.AddAuthentication().AddDiscord(Scheme, discord =>
        {
            discord.ClientId = options.ClientId!;
            discord.ClientSecret = options.ClientSecret!;
            discord.CallbackPath = CallbackPath;
            discord.SignInScheme = IdentityConstants.ExternalScheme;
            discord.UsePkce = true;
            discord.SaveTokens = false;

            // Only who the user is: no e-mail, no guilds.
            discord.Scope.Clear();
            discord.Scope.Add("identify");

            // Lax: the cookie must come back with Discord's redirect from another site.
            discord.CorrelationCookie.SameSite = SameSiteMode.Lax;
            discord.CorrelationCookie.SecurePolicy = secure;

            // Cancelled on Discord, or an expired or forged state: back to where the user came from, with a message.
            discord.Events.OnRemoteFailure = context =>
            {
                var linking = context.Properties?.Items.TryGetValue(PurposeKey, out var purpose) == true && purpose == Link;
                context.Response.Redirect(linking ? LinkFailedPath : LoginFailedPath);
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });

        // Holds who Discord said the user is between the callback and CompletePath; Lax for the same reason.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme, external =>
        {
            external.Cookie.Name = "cf_external";
            external.Cookie.HttpOnly = true;
            external.Cookie.SameSite = SameSiteMode.Lax;
            external.Cookie.SecurePolicy = secure;
            external.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
    }

    /// <summary>Properties of the challenge that signs in; <paramref name="redirect"/> is where to go afterwards.</summary>
    public static AuthenticationProperties LoginProperties(string? redirect) => new()
    {
        RedirectUri = CompletePath,
        Items = { [PurposeKey] = Login, [RedirectKey] = SafePath(redirect) },
    };

    /// <summary>Properties of the challenge that links the signed-in user's account; only from a session.</summary>
    /// <exception cref="UnauthenticatedException">Nobody is signed in.</exception>
    public async Task<AuthenticationProperties> LinkPropertiesAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        return new AuthenticationProperties
        {
            RedirectUri = CompletePath,
            Items = { [PurposeKey] = Link, [UserKey] = user.Id.Value.ToString() },
        };
    }

    /// <summary>Finishes the round trip: signs in or links, and says where the browser goes next.</summary>
    public async Task<string> CompleteAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
        await http.SignOutAsync(IdentityConstants.ExternalScheme);
        var properties = external.Properties;
        var purpose = Item(properties, PurposeKey);
        var discordId = ulong.TryParse(external.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (ulong?)null;
        var discordName = external.Principal?.FindFirstValue(ClaimTypes.Name) ?? "";

        if (!external.Succeeded || discordId is null)
        {
            return purpose == Link ? LinkFailedPath : LoginFailedPath;
        }

        return purpose switch
        {
            Link => await LinkAsync(Item(properties, UserKey), discordId.Value, discordName, cancellationToken),
            _ => await SignInAsync(discordId.Value, discordName, Item(properties, RedirectKey), cancellationToken),
        };
    }

    /// <summary>
    /// A page that goes on to <paramref name="path"/> from the aggregator's own site, so the SameSite=Strict session
    /// cookie set during Discord's redirects is sent with it.
    /// </summary>
    public static IResult ContinuePage(HttpContext http, string path)
    {
        var target = HtmlEncoder.Default.Encode(SafePath(path));
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Content(
            $"""<!doctype html><meta charset="utf-8"><meta http-equiv="refresh" content="0;url={target}"><title>container-forest</title><p><a href="{target}">Continue</a></p>""",
            "text/html; charset=utf-8");
    }

    /// <summary>A path on this site, never another site: "/x" yes, "//evil.example" and "https://…" no.</summary>
    public static string SafePath(string? path) =>
        path is { Length: > 0 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal)
            ? path
            : "/";

    private static string? Item(AuthenticationProperties? properties, string key) =>
        properties is not null && properties.Items.TryGetValue(key, out var value) ? value : null;

    private async Task<string> SignInAsync(ulong discordId, string discordName, string? redirect, CancellationToken cancellationToken)
    {
        // A Discord account linked to nobody gets no user (no sign-up through Discord); every refusal looks the same.
        var user = await links.FindUserForSignInAsync(discordId, discordName, cancellationToken);
        var account = user is null ? null : await signInManager.UserManager.FindByIdAsync(user.Id.Value.ToString());
        if (account is null || await signInManager.UserManager.IsLockedOutAsync(account))
        {
            return LoginFailedPath;
        }

        await signInManager.SignInAsync(account, isPersistent: false, authenticationMethod: Scheme);
        return SafePath(redirect);
    }

    private async Task<string> LinkAsync(string? userId, ulong discordId, string discordName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return LinkFailedPath;
        }

        try
        {
            await links.LinkVerifiedAsync(Domain.Common.UserId.From(id), discordId, discordName, cancellationToken);
            return LinkedPath;
        }
        catch (Exception exception) when (exception is AccessDeniedException or Application.Common.NotFoundException)
        {
            logger.LogInformation("Discord link for user {UserId} refused: {Reason}", id, exception.Message);
            return LinkFailedPath;
        }
    }
}
