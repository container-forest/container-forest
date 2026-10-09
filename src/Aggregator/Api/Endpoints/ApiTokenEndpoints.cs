using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Personal API tokens, see ApiTokenContracts.</summary>
internal static class ApiTokenEndpoints
{
    public static IEndpointRouteBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/me/tokens")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mine.MapGet("/", async (ApiTokenService service, TimeProvider time, CancellationToken ct) =>
                (await service.ListMineAsync(ct)).Select(t => ToResponse(t, time.GetUtcNow())).ToList())
            .WithName("ListApiTokens");

        mine.MapPost("/", async (CreateApiTokenRequest request, ApiTokenService service, TimeProvider time, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(
                    request.Name,
                    request.ReadOnly,
                    request.EnvironmentIds?.Select(EnvironmentId.From).ToList(),
                    request.ExpiresAt,
                    ct);
                return TypedResults.Created(
                    $"/api/me/tokens/{created.Token.Id}",
                    new CreatedApiTokenResponse(ToResponse(created.Token, time.GetUtcNow()), created.Secret));
            })
            .WithName("CreateApiToken")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapDelete("/current", async (ApiTokenService service, CancellationToken ct) =>
            {
                await service.RevokeCurrentAsync(ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeCurrentApiToken")
            .AllowReadOnlyTokens()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mine.MapDelete("/{id:guid}", async (Guid id, ApiTokenService service, CancellationToken ct) =>
            {
                await service.RevokeMineAsync(ApiTokenId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeApiToken")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var users = app.MapGroup("/api/users/{userId:guid}/tokens")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/", async (Guid userId, ApiTokenService service, TimeProvider time, CancellationToken ct) =>
                (await service.ListForUserAsync(UserId.From(userId), ct)).Select(t => ToResponse(t, time.GetUtcNow())).ToList())
            .WithName("ListUserApiTokens");

        users.MapDelete("/{tokenId:guid}", async (Guid userId, Guid tokenId, ApiTokenService service, CancellationToken ct) =>
            {
                await service.RevokeForUserAsync(UserId.From(userId), ApiTokenId.From(tokenId), ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeUserApiToken");

        return app;
    }

    private static ApiTokenResponse ToResponse(ApiToken token, DateTimeOffset now) => new(
        token.Id.Value,
        token.Name,
        token.Hint,
        token.IsReadOnly,
        token.IsRestricted ? token.Environments.Select(e => e.Value).ToList() : null,
        token.CreatedAt,
        token.ExpiresAt,
        token.LastUsedAt,
        token.IsExpired(now));
}
