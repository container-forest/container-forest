using System.Security.Claims;
using System.Text.Encodings.Web;
using ContainerForest.Aggregator.Application.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// Stand-in for the login until it exists: the X-Dev-User header names the acting user, no password.
/// Registered only in the Development environment.
/// </summary>
internal sealed class DevUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IUserRepository users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevUser";
    public const string HeaderName = "X-Dev-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userName = Request.Headers[HeaderName].ToString();
        if (userName.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (await users.FindByUserNameAsync(userName, Context.RequestAborted) is not { } user)
        {
            return AuthenticateResult.Fail($"Unknown dev user '{userName}'.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.Value.ToString()), new Claim(ClaimTypes.Name, user.UserName)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
