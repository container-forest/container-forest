using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

internal static class AuthenticationSetup
{
    public const string SessionCookieName = "cf_session";

    /// <summary>
    /// Picks the scheme of a request: an API token when it has a bearer credential, otherwise (in Development) the dev
    /// header when it has one, otherwise the session cookie.
    /// </summary>
    private const string DefaultScheme = "Default";

    /// <summary>Requires a session (cookie or, in Development, the dev header), not an API token: the live hub.</summary>
    public const string SessionPolicy = "Session";

    /// <summary>
    /// The <c>cf_session</c> cookie of a password sign-in and the <c>Authorization: Bearer</c> API tokens of the CLI.
    /// In Development the X-Dev-User header keeps working next to them, so the rest of the team can act as any user
    /// without a password.
    /// </summary>
    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        new IdentityBuilder(typeof(AccountUser), services).AddSignInManager();
        services.AddScoped<PasswordSignIn>();

        // Check the security stamp on every request: a password change or a deactivation must end the user's
        // other sessions right away, not after the default 30 minutes. The price is a lookup per request.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

        var authentication = services.AddAuthentication(DefaultScheme);
        authentication.AddIdentityCookies();
        authentication.AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null);
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;

            // Locally and in tests the aggregator runs over plain http.
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            // An API has no login page to redirect to.
            options.Events.OnRedirectToLogin = context => WriteStatus(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => WriteStatus(context, StatusCodes.Status403Forbidden);
        });

        var isDevelopment = environment.IsDevelopment();
        if (isDevelopment)
        {
            authentication.AddScheme<AuthenticationSchemeOptions, DevUserAuthenticationHandler>(DevUserAuthenticationHandler.SchemeName, null);
        }

        authentication.AddPolicyScheme(DefaultScheme, null, options => options.ForwardDefaultSelector = context =>
            ApiTokenAuthenticationHandler.HasBearer(context.Request) ? ApiTokenAuthenticationHandler.SchemeName
            : isDevelopment && context.Request.Headers.ContainsKey(DevUserAuthenticationHandler.HeaderName) ? DevUserAuthenticationHandler.SchemeName
            : IdentityConstants.ApplicationScheme);

        services.AddAuthorizationBuilder().AddPolicy(SessionPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => ApiTokenClaims.TokenIdOf(context.User) is null));

        return services;
    }

    private static Task WriteStatus(RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
