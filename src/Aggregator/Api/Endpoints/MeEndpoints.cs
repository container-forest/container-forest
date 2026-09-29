using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;

namespace ContainerForest.Aggregator.Api.Endpoints;

internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (CurrentActor currentActor, CancellationToken ct) =>
            {
                var user = await currentActor.GetAsync(ct);
                return new MeResponse(user.Id.Value, user.UserName, user.IsAdmin);
            })
            .WithName("GetMe")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
