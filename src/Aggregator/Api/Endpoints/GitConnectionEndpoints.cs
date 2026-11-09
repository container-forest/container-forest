using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Git connections for the panel (see GitConnectionContracts).</summary>
internal static class GitConnectionEndpoints
{
    public static IEndpointRouteBuilder MapGitConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/git-connections")
            .WithTags("Git connections")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        connections.MapGet("/", async (GitConnectionService service, CancellationToken ct) =>
                (await service.ListAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListGitConnections");

        connections.MapPost("/", async (CreateGitConnectionRequest request, GitConnectionService service, CancellationToken ct) =>
            {
                var connection = await service.CreateAsync(
                    request.Name,
                    request.UrlPrefix,
                    ToAccessToken(request.AccessToken),
                    ToEnvironments(request.AllEnvironments, request.EnvironmentIds),
                    ct);
                return TypedResults.Created($"/api/git-connections/{connection.Connection.Id}", ToResponse(connection));
            })
            .WithName("CreateGitConnection")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        connections.MapPut(
                "/{id:guid}",
                async (Guid id, UpdateGitConnectionRequest request, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.UpdateAsync(
                        GitConnectionId.From(id),
                        request.Name,
                        request.UrlPrefix,
                        ToEnvironments(request.AllEnvironments, request.EnvironmentIds),
                        ct)))
            .WithName("UpdateGitConnection")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        connections.MapDelete(
                "/{id:guid}",
                async (Guid id, GitConnectionService service, CancellationToken ct) =>
                {
                    await service.DeleteAsync(GitConnectionId.From(id), ct);
                    return TypedResults.NoContent();
                })
            .WithName("DeleteGitConnection")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        connections.MapPut(
                "/{id:guid}/access-token",
                async (Guid id, GitAccessTokenRequest request, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.SetAccessTokenAsync(GitConnectionId.From(id), ToAccessToken(request)!, ct)))
            .WithName("SetGitConnectionAccessToken")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        connections.MapDelete(
                "/{id:guid}/access-token",
                async (Guid id, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.RemoveAccessTokenAsync(GitConnectionId.From(id), ct)))
            .WithName("RemoveGitConnectionAccessToken")
            .ProducesProblem(StatusCodes.Status404NotFound);

        connections.MapPut(
                "/{id:guid}/push-credential",
                async (Guid id, GitAccessTokenRequest request, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.SetPushCredentialAsync(GitConnectionId.From(id), ToAccessToken(request)!, ct)))
            .WithName("SetGitConnectionPushCredential")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        connections.MapDelete(
                "/{id:guid}/push-credential",
                async (Guid id, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.RemovePushCredentialAsync(GitConnectionId.From(id), ct)))
            .WithName("RemoveGitConnectionPushCredential")
            .ProducesProblem(StatusCodes.Status404NotFound);

        connections.MapPost(
                "/{id:guid}/webhook",
                async (Guid id, GitConnectionService service, CancellationToken ct) =>
                {
                    var (connection, secret) = await service.CreateWebhookSecretAsync(GitConnectionId.From(id), ct);
                    return new GitConnectionWebhookSecretResponse(ToResponse(connection), secret);
                })
            .WithName("CreateGitConnectionWebhookSecret")
            .ProducesProblem(StatusCodes.Status404NotFound);

        connections.MapDelete(
                "/{id:guid}/webhook",
                async (Guid id, GitConnectionService service, CancellationToken ct) =>
                    ToResponse(await service.RemoveWebhookSecretAsync(GitConnectionId.From(id), ct)))
            .WithName("RemoveGitConnectionWebhookSecret")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static GitConnectionResponse ToResponse(GitConnectionSummary summary) => new(
        summary.Connection.Id.Value,
        summary.Connection.Name,
        summary.Connection.UrlPrefix.ToString(),
        summary.Connection.HasAccessToken,
        summary.Connection.AccessUserName,
        summary.Connection.AllowsAllEnvironments,
        summary.EnvironmentIds.Select(id => id.Value).Order().ToList(),
        summary.Connection.HasWebhook,
        summary.RepositoryCount,
        summary.Connection.CreatedAt,
        summary.Connection.HasPushCredential,
        summary.Connection.PushUserName);

    private static GitAccessToken? ToAccessToken(GitAccessTokenRequest? request) =>
        request is null ? null : new GitAccessToken(request.UserName, request.Token ?? "");

    private static ConnectionEnvironments ToEnvironments(bool all, IReadOnlyList<Guid>? ids) =>
        new(all, (ids ?? []).Select(EnvironmentId.From).ToList());
}
