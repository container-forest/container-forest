using ContainerForest.Aggregator.Application.Audit;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// <see cref="IRequestOrigin"/> from the HTTP connection. Behind Caddy the address is the client's, taken from
/// <c>X-Forwarded-For</c> by the forwarded headers middleware (see deploy/aggregator/compose.yaml).
/// </summary>
internal sealed class HttpRequestOrigin(IHttpContextAccessor httpContextAccessor) : IRequestOrigin
{
    /// <summary>The authentication type the bot gateway gives the user it acts for (BotGatewayService).</summary>
    public const string BotAuthenticationType = "DiscordBot";

    public bool IsBot => httpContextAccessor.HttpContext?.User.Identity?.AuthenticationType == BotAuthenticationType;

    public string? Address => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress is { } address
        ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
        : null;
}
