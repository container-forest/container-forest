using System.Security.Claims;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// <see cref="ICurrentUser"/> for HTTP requests. Any authentication scheme (the login cookie, the dev header)
/// must put the user's id into the <see cref="ClaimTypes.NameIdentifier"/> claim; this is the only contract
/// between authentication and authorization.
/// </summary>
internal sealed class ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public UserId? Id =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? UserId.From(id)
            : null;
}
