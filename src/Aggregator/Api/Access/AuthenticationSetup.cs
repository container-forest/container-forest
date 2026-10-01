using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

internal static class AuthenticationSetup
{
    public const string SessionCookieName = "cf_session";

    /// <summary>Development only: the dev header when the request has one, the session cookie otherwise.</summary>
    private const string DevOrCookieScheme = "DevOrCookie";

    /// <summary>
    /// The <c>cf_session</c> cookie of a password sign-in. In Development the X-Dev-User header keeps working
    /// next to it, so the rest of the team can act as any user without a password.
    /// </summary>
    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        new IdentityBuilder(typeof(AccountUser), services).AddSignInManager();
        services.AddScoped<PasswordSignIn>();

        // Check the security stamp on every request: a password change or a deactivation must end the user's
        // other sessions right away, not after the default 30 minutes. The price is a lookup per request.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

        var authentication = services.AddAuthentication(
            environment.IsDevelopment() ? DevOrCookieScheme : IdentityConstants.ApplicationScheme);
        authentication.AddIdentityCookies();
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

        if (environment.IsDevelopment())
        {
            authentication
                .AddScheme<AuthenticationSchemeOptions, DevUserAuthenticationHandler>(DevUserAuthenticationHandler.SchemeName, null)
                .AddPolicyScheme(DevOrCookieScheme, null, options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(DevUserAuthenticationHandler.HeaderName)
                        ? DevUserAuthenticationHandler.SchemeName
                        : IdentityConstants.ApplicationScheme);
        }

        return services;
    }

    private static Task WriteStatus(RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
