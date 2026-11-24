using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>The signed-in user as the panel needs them, with their two-factor state.</summary>
internal sealed class MeService(TwoFactorManagement twoFactor, IHttpContextAccessor httpContext)
{
    /// <param name="method">
    /// How the session was signed in (<see cref="SignInMethods"/>): a sign-in passes the method it just used, since the
    /// request itself has no session yet; otherwise the request's own session is asked.
    /// </param>
    public async Task<MeResponse> DescribeAsync(User user, CancellationToken cancellationToken, string? method = null) => new(
        user.Id.Value,
        user.UserName,
        user.IsAdmin,
        await twoFactor.IsEnabledAsync(user.Id),
        await twoFactor.RequiresSetupAsync(
            user, method ?? (httpContext.HttpContext?.User is { } principal ? SignInMethods.Of(principal) : null), cancellationToken));
}
