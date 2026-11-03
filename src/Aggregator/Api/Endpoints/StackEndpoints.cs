using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Repositories, stacks and deployments for the panel (see StackContracts).</summary>
internal static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStackEndpoints(this IEndpointRouteBuilder app)
    {
        var repositories = app.MapGroup("/api/repositories")
            .WithTags("Repositories")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        repositories.MapGet("/", async (RepositoryService service, CancellationToken ct) =>
                (await service.ListAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListRepositories");

        repositories.MapPost("/", async (CreateRepositoryRequest request, RepositoryService service, CancellationToken ct) =>
            {
                var repository = await service.CreateAsync(request.Name, request.Url, ConnectionIdOf(request.ConnectionId), ct);
                return TypedResults.Created($"/api/repositories/{repository.Repository.Id}", ToResponse(repository));
            })
            .WithName("CreateRepository")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        repositories.MapPut(
                "/{id:guid}",
                async (Guid id, UpdateRepositoryRequest request, RepositoryService service, CancellationToken ct) =>
                    ToResponse(await service.UpdateAsync(
                        RepositoryId.From(id), request.Name, request.Url, ConnectionIdOf(request.ConnectionId), ct)))
            .WithName("UpdateRepository")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        repositories.MapDelete(
                "/{id:guid}",
                async (Guid id, RepositoryService service, CancellationToken ct) =>
                {
                    await service.RemoveAsync(RepositoryId.From(id), ct);
                    return TypedResults.NoContent();
                })
            .WithName("RemoveRepository")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        repositories.MapPost(
                "/{id:guid}/webhook",
                async (Guid id, RepositoryService service, CancellationToken ct) =>
                {
                    var (repository, secret) = await service.CreateWebhookSecretAsync(RepositoryId.From(id), ct);
                    return new WebhookSecretResponse(ToResponse(repository), secret);
                })
            .WithName("CreateRepositoryWebhookSecret")
            .ProducesProblem(StatusCodes.Status404NotFound);

        repositories.MapDelete(
                "/{id:guid}/webhook",
                async (Guid id, RepositoryService service, CancellationToken ct) =>
                    ToResponse(await service.RemoveWebhookSecretAsync(RepositoryId.From(id), ct)))
            .WithName("RemoveRepositoryWebhookSecret")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var api = app.MapGroup("/api")
            .WithTags("Stacks")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/environments/{id:guid}/stacks", async (Guid id, StackService service, CancellationToken ct) =>
                (await service.ListVisibleAsync(EnvironmentId.From(id), ct)).Select(ToResponse).ToList())
            .WithName("ListStacks");

        api.MapPost(
                "/environments/{id:guid}/stacks",
                async (Guid id, CreateStackRequest request, StackService service, CancellationToken ct) =>
                {
                    var stack = await service.CreateAsync(
                        EnvironmentId.From(id), request.Name, RepositoryId.From(request.RepositoryId), request.Branch, request.ComposePath, ct);
                    return TypedResults.Created($"/api/stacks/{stack.Id}", ToResponse(stack));
                })
            .WithName("CreateStack")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/stacks/{id:guid}", async (Guid id, StackService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(StackId.From(id), ct)))
            .WithName("GetStack");

        api.MapDelete(
                "/stacks/{id:guid}",
                async (Guid id, StackService service, CancellationToken ct) =>
                {
                    await service.RemoveAsync(StackId.From(id), ct);
                    return TypedResults.NoContent();
                })
            .WithName("RemoveStack")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/stacks/{id:guid}/source",
                async (Guid id, ChangeStackSourceRequest request, StackService service, CancellationToken ct) =>
                    ToResponse(await service.ChangeSourceAsync(
                        StackId.From(id), RepositoryId.From(request.RepositoryId), request.Branch, request.ComposePath, ct)))
            .WithName("ChangeStackSource")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/stacks/{id:guid}/deploy-on-push",
                async (Guid id, DeployOnPushRequest request, StackService service, CancellationToken ct) =>
                    ToResponse(await service.SetDeployOnPushAsync(StackId.From(id), request.Enabled, ct)))
            .WithName("SetStackDeployOnPush")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/stacks/{id:guid}/restart-on-file-change",
                async (Guid id, RestartOnFileChangeRequest request, StackService service, CancellationToken ct) =>
                    ToResponse(await service.SetRestartOnFileChangeAsync(StackId.From(id), request.Enabled, ct)))
            .WithName("SetStackRestartOnFileChange")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/stacks/{id:guid}/require-signed-commits",
                async (Guid id, RequireSignedCommitsRequest request, StackService service, CancellationToken ct) =>
                    ToResponse(await service.SetRequireSignedCommitsAsync(StackId.From(id), request.Enabled, ct)))
            .WithName("SetStackRequireSignedCommits")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/stacks/{id:guid}/require-image-digests",
                async (Guid id, RequireImageDigestsRequest request, StackService service, CancellationToken ct) =>
                    ToResponse(await service.SetRequireImageDigestsAsync(StackId.From(id), request.Enabled, ct)))
            .WithName("SetStackRequireImageDigests")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost(
                "/stacks/{id:guid}/deploy-hook",
                async (Guid id, StackService service, CancellationToken ct) =>
                {
                    var (stack, token) = await service.CreateDeployHookAsync(StackId.From(id), ct);
                    return new DeployHookTokenResponse(ToResponse(stack), token);
                })
            .WithName("CreateStackDeployHook")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete(
                "/stacks/{id:guid}/deploy-hook",
                async (Guid id, StackService service, CancellationToken ct) =>
                    ToResponse(await service.RemoveDeployHookAsync(StackId.From(id), ct)))
            .WithName("RemoveStackDeployHook");

        api.MapStackAction("restart", "RestartStack", StackAction.Restart);
        api.MapStackAction("down", "DownStack", StackAction.Down);

        api.MapGet("/stacks/{id:guid}/deployments", async (Guid id, DeploymentService service, CancellationToken ct) =>
                (await service.ListAsync(StackId.From(id), ct))
                    .Select(d => new DeploymentResponse(
                        d.Id.Value, d.Status, d.Trigger, d.PullPolicy, d.Commit?.Value, d.QueuedAt, d.StartedAt, d.FinishedAt, d.Error,
                        d.Endorsement, d.SignerName))
                    .ToList())
            .WithName("ListDeployments");

        api.MapPost(
                "/stacks/{id:guid}/deployments",
                async (Guid id, DeployRequest request, DeploymentService service, CancellationToken ct) =>
                {
                    var deployment = await service.QueueAsync(
                        StackId.From(id),
                        request.Pull ? ImagePullPolicy.Always : ImagePullPolicy.Missing,
                        ct,
                        request.ApproveCommit is { } approved ? CommitSha.Parse(approved) : null);
                    return TypedResults.Accepted($"/api/deployments/{deployment.Id}", ToDetails(deployment));
                })
            .WithName("DeployStack")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/deployments/{id:guid}", async (Guid id, DeploymentService service, CancellationToken ct) =>
                ToDetails(await service.GetAsync(DeploymentId.From(id), ct)))
            .WithName("GetDeployment");

        return app;
    }

    /// <summary>One endpoint per action, as for containers, so the typed client names them.</summary>
    private static void MapStackAction(this RouteGroupBuilder api, string verb, string name, StackAction action) =>
        api.MapPost(
                $"/stacks/{{id:guid}}/{verb}",
                async (Guid id, int? timeoutSeconds, StackCommandService service, CancellationToken ct) =>
                {
                    var stopTimeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
                    var result = await service.ExecuteAsync(StackId.From(id), action, stopTimeout, ct);
                    return new StackCommandResponse(result.Succeeded, result.Error, result.Output);
                })
            .WithName(name)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

    private static RepositoryResponse ToResponse(RepositorySummary summary) => new(
        summary.Repository.Id.Value,
        summary.Repository.Name,
        summary.Repository.Url.ToString(),
        summary.Connection?.Id.Value,
        summary.Connection?.Name,
        summary.Repository.HasWebhook,
        summary.Connection?.HasWebhook ?? false,
        summary.CanManage,
        summary.StackCount,
        summary.Repository.CreatedAt);

    private static GitConnectionId? ConnectionIdOf(Guid? id) => id is { } value ? GitConnectionId.From(value) : null;

    private static StackResponse ToResponse(StackSummary s) => new(
        s.Id.Value,
        s.EnvironmentId.Value,
        s.Name.Value,
        s.RepositoryId.Value,
        s.RepositoryName,
        s.Branch.Value,
        s.ComposePath.Value,
        s.IsSystem,
        s.DeployedCommit?.Value,
        s.DeployedAt,
        s.DeployOnPush,
        s.RestartOnFileChange,
        s.HasDeployHook,
        s.RequireSignedCommits,
        s.RequireImageDigests,
        s.CanDeploy,
        s.CanRestart,
        s.CanRemove);

    private static DeploymentDetailsResponse ToDetails(Deployment d) => new(
        d.Id.Value,
        d.StackId.Value,
        d.Status,
        d.Trigger,
        d.PullPolicy,
        d.Commit?.Value,
        d.QueuedAt,
        d.StartedAt,
        d.FinishedAt,
        d.Error,
        d.Log.Select(l => new DeploymentLogLineResponse(l.At, l.Level, l.Text)).ToList(),
        new EndorsementResponse(d.Endorsement, d.EndorsementProblem, d.SignerName, d.SigningKeyFingerprint, d.ApprovedCommit?.Value));
}
