using System.Security.Claims;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// <see cref="ICurrentUser"/> for HTTP requests. Any authentication scheme (the login cookie, the dev header)
/// must put the user's id into the <see cref="ClaimTypes.NameIdentifier"/> claim; this is the only contract
/// between authentication and authorization. An API token adds its limits (<see cref="ApiTokenClaims"/>).
/// </summary>
/// <remarks>
/// A SignalR hub invocation has no current HTTP request to read: the hub passes its connection's user with
/// <see cref="Use"/> instead, for the scope of the invocation.
/// </remarks>
internal sealed class ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? principal;

    private ClaimsPrincipal? Principal => principal ?? httpContextAccessor.HttpContext?.User;

    public UserId? Id =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? UserId.From(id)
            : null;

    public AccessScope Scope => ApiTokenClaims.ScopeOf(Principal);

    public ApiTokenId? ApiTokenId => ApiTokenClaims.TokenIdOf(Principal);

    public void Use(ClaimsPrincipal user) => principal = user;
}
