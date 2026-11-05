using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Age keys of environments for the panel (see AgeKeyContracts).</summary>
internal static class AgeKeyEndpoints
{
    public static IEndpointRouteBuilder MapAgeKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/environments/{id:guid}/age-keys")
            .WithTags("Age keys")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", async (Guid id, AgeKeyService service, CancellationToken ct) =>
                (await service.ListAsync(EnvironmentId.From(id), ct)).Select(ToResponse).ToList())
            .WithName("ListAgeKeys");

        // ?stackId= creates a key of one stack of the environment (ADR-0012).
        group.MapPost("/", async (Guid id, Guid? stackId, AgeKeyService service, CancellationToken ct) =>
            {
                var key = await service.CreateAsync(EnvironmentId.From(id), stackId is { } s ? StackId.From(s) : null, ct);
                return TypedResults.Created($"/api/environments/{id}/age-keys/{key.Id}", ToResponse(key));
            })
            .WithName("CreateAgeKey");

        group.MapDelete("/{keyId:guid}", async (Guid id, Guid keyId, AgeKeyService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(EnvironmentId.From(id), AgeKeyId.From(keyId), ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteAgeKey");

        return app;
    }

    private static AgeKeyResponse ToResponse(AgeKeySummary key) => new(key.Id.Value, key.Recipient, key.CreatedAt, key.StackId?.Value);
}
