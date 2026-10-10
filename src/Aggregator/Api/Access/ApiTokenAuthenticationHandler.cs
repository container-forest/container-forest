using System.Security.Claims;
using System.Text.Encodings.Web;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// <c>Authorization: Bearer cf_pat_…</c>: a personal API token of the CLI or a script. The token's limits travel
/// as claims (<see cref="ApiTokenClaims"/>) to <see cref="ClaimsCurrentUser"/>, and from there to every permission check.
/// </summary>
/// <remarks>Neither the token nor its hash is logged.</remarks>
internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenAuthenticator authenticator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    private const string BearerPrefix = "Bearer ";

    /// <summary>True when the request carries a bearer credential, which this scheme then has to accept or reject.</summary>
    public static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        // Other bearer secrets (a stack's deploy hook) are checked by their own endpoints.
        var secret = header[BearerPrefix.Length..].Trim();
        if (!ApiToken.HasTokenShape(secret))
        {
            return AuthenticateResult.NoResult();
        }

        var token = await authenticator.AuthenticateAsync(secret, Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Unknown or expired API token.");
        }

        var identity = new ClaimsIdentity(ApiTokenClaims.For(token), SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer");
        return Task.CompletedTask;
    }
}

/// <summary>How an API token's identity and limits are written into, and read back from, the request's claims.</summary>
internal static class ApiTokenClaims
{
    public const string TokenId = "cf:api-token";
    public const string ReadOnly = "cf:read-only";
    public const string Restricted = "cf:restricted";
    public const string Environment = "cf:environment";
    public const string Secrets = "cf:secrets";

    public static IEnumerable<Claim> For(ApiToken token)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, token.UserId.Value.ToString());
        yield return new Claim(TokenId, token.Id.Value.ToString());
        if (token.IsReadOnly)
        {
            yield return new Claim(ReadOnly, "true");
        }

        if (token.CanDecryptSecrets)
        {
            yield return new Claim(Secrets, "true");
        }

        if (token.IsRestricted)
        {
            // Separate from the environment claims: a restricted token whose environments are all gone reaches none.
            yield return new Claim(Restricted, "true");
            foreach (var environmentId in token.Environments)
            {
                yield return new Claim(Environment, environmentId.Value.ToString());
            }
        }
    }

    public static ApiTokenId? TokenIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(TokenId), out var id) ? ApiTokenId.From(id) : null;

    public static bool IsReadOnly(ClaimsPrincipal? principal) => principal?.HasClaim(ReadOnly, "true") == true;

    public static AccessScope ScopeOf(ClaimsPrincipal? principal)
    {
        if (principal is null || TokenIdOf(principal) is null)
        {
            return AccessScope.Full;
        }

        var environments = principal.HasClaim(Restricted, "true")
            ? principal.FindAll(Environment).Select(c => EnvironmentId.From(Guid.Parse(c.Value)))
            : null;
        return new AccessScope(environments, IsReadOnly(principal), principal.HasClaim(Secrets, "true"));
    }
}
