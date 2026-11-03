using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Signing keys that endorse commits, see SigningKeyContracts.</summary>
internal static class SigningKeyEndpoints
{
    public static IEndpointRouteBuilder MapSigningKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/me/signing-keys")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mine.MapGet("/", async (SigningKeyService service, CancellationToken ct) =>
                (await service.ListMineAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListSigningKeys");

        mine.MapPost("/", async (AddSigningKeyRequest request, SigningKeyService service, CancellationToken ct) =>
            {
                var added = await service.AddMineAsync(request.Name, request.PublicKey, EnvironmentIdsOf(request.EnvironmentIds), ct);
                return TypedResults.Created($"/api/me/signing-keys/{added.Key.Id}", ToResponse(added));
            })
            .WithName("AddSigningKey")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapPut("/{id:guid}", async (Guid id, UpdateSigningKeyRequest request, SigningKeyService service, CancellationToken ct) =>
                ToResponse(await service.UpdateMineAsync(SigningKeyId.From(id), request.Name, EnvironmentIdsOf(request.EnvironmentIds), ct)))
            .WithName("UpdateSigningKey")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mine.MapDelete("/{id:guid}", async (Guid id, SigningKeyService service, CancellationToken ct) =>
            {
                await service.RemoveMineAsync(SigningKeyId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("RemoveSigningKey")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var users = app.MapGroup("/api/users/{userId:guid}/signing-keys")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/", async (Guid userId, SigningKeyService service, CancellationToken ct) =>
                (await service.ListForUserAsync(UserId.From(userId), ct)).Select(ToResponse).ToList())
            .WithName("ListUserSigningKeys");

        users.MapPost("/", async (Guid userId, AddSigningKeyRequest request, SigningKeyService service, CancellationToken ct) =>
            {
                var added = await service.AddForUserAsync(
                    UserId.From(userId), request.Name, request.PublicKey, EnvironmentIdsOf(request.EnvironmentIds), ct);
                return TypedResults.Created($"/api/users/{userId}/signing-keys/{added.Key.Id}", ToResponse(added));
            })
            .WithName("AddUserSigningKey")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapDelete("/{keyId:guid}", async (Guid userId, Guid keyId, SigningKeyService service, CancellationToken ct) =>
            {
                await service.RemoveForUserAsync(UserId.From(userId), SigningKeyId.From(keyId), ct);
                return TypedResults.NoContent();
            })
            .WithName("RemoveUserSigningKey");

        return app;
    }

    private static List<EnvironmentId>? EnvironmentIdsOf(IReadOnlyList<Guid>? ids) => ids?.Select(EnvironmentId.From).ToList();

    private static SigningKeyResponse ToResponse(SigningKeySummary summary)
    {
        var key = summary.Key;
        return new SigningKeyResponse(
            key.Id.Value,
            key.Name,
            key.PublicKey,
            key.Fingerprint,
            key.IsHardwareBacked,
            key.IsRestricted ? key.Environments.Select(e => e.Value).ToList() : null,
            summary.AddedByAdministrator,
            key.CreatedAt,
            key.LastUsedAt);
    }
}
