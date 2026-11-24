using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;

namespace ContainerForest.Aggregator.Api.Endpoints;

internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (CurrentActor currentActor, MeService me, CancellationToken ct) =>
                await me.DescribeAsync((await currentActor.GetAsync(ct)).User, ct))
            .WithName("GetMe")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
