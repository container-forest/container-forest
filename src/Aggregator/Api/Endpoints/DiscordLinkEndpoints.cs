using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>The signed-in user's Discord link, see <see cref="DiscordLinkResponse"/>.</summary>
internal static class DiscordLinkEndpoints
{
    public static IEndpointRouteBuilder MapDiscordLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/discord")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", async (DiscordLinkService links, CancellationToken ct) =>
            {
                var status = await links.GetStatusAsync(ct);
                return new DiscordLinkResponse(status.Linked, status.DiscordUserName, status.LinkedAt);
            })
            .WithName("GetDiscordLink");

        group.MapPost("/link-code", async (DiscordLinkService links, CancellationToken ct) =>
            {
                var code = await links.IssueCodeAsync(ct);
                return TypedResults.Created("/api/me/discord", new DiscordLinkCodeResponse(code.Code, code.ExpiresAt));
            })
            .WithName("IssueDiscordLinkCode");

        group.MapDelete("/", async (DiscordLinkService links, CancellationToken ct) =>
            {
                await links.UnlinkAsync(ct);
                return TypedResults.NoContent();
            })
            .WithName("UnlinkDiscord");

        return app;
    }
}
