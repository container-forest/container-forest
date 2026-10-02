using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Temporary endpoints for repositories, stacks and deployments until the panel API exists. Like
/// <see cref="DevEnvironmentEndpoints"/>, they act as the X-Dev-User user and are mapped only in Development.
/// </summary>
internal static class DevStackEndpoints
{
    public static IEndpointRouteBuilder MapDevStackEndpoints(this IEndpointRouteBuilder app)
    {
        var repositories = app.MapGroup("/api/dev/repositories").WithTags("Dev: repositories");

        repositories.MapGet("/", async (RepositoryService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(ToResponse));

        repositories.MapPost("/", async (CreateRepositoryRequest request, RepositoryService service, CancellationToken ct) =>
        {
            var repository = await service.CreateAsync(request.Name, request.Url, ct);
            return TypedResults.Created($"/api/dev/repositories/{repository.Id}", ToResponse(repository));
        });

        var stacks = app.MapGroup("/api/dev").WithTags("Dev: stacks");

        stacks.MapGet("/environments/{id:guid}/stacks", async (Guid id, StackService service, CancellationToken ct) =>
            (await service.ListVisibleAsync(EnvironmentId.From(id), ct)).Select(s => new StackResponse(
                s.Id.Value,
                s.Name.Value,
                s.RepositoryId.Value,
                s.Branch.Value,
                s.ComposePath.Value,
                s.IsSystem,
                s.DeployedCommit?.Value,
                s.DeployedAt,
                s.CanDeploy,
                s.CanRestart)));

        stacks.MapPost(
            "/environments/{id:guid}/stacks",
            async (Guid id, CreateStackRequest request, StackService service, CancellationToken ct) =>
            {
                var stack = await service.CreateAsync(
                    EnvironmentId.From(id), request.Name, RepositoryId.From(request.RepositoryId), request.Branch, request.ComposePath, ct);
                return TypedResults.Created(
                    $"/api/dev/stacks/{stack.Id}",
                    new StackResponse(
                        stack.Id.Value,
                        stack.Name.Value,
                        stack.RepositoryId.Value,
                        stack.Branch.Value,
                        stack.ComposePath.Value,
                        stack.IsSystem,
                        DeployedCommit: null,
                        DeployedAt: null,
                        CanDeploy: true,
                        CanRestart: true));
            });

        // Answers 200 also when compose itself failed: the output says why. Errors before compose ran
        // (no access, system stack, agent offline or busy, stack never deployed) are problem responses.
        stacks.MapPost(
            "/stacks/{id:guid}/{action}",
            async (Guid id, string action, int? timeoutSeconds, StackCommandService service, CancellationToken ct) =>
            {
                if (!Enum.TryParse<StackAction>(action, ignoreCase: true, out var stackAction) || !Enum.IsDefined(stackAction))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Action must be restart or down.");
                }

                var stopTimeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
                var result = await service.ExecuteAsync(StackId.From(id), stackAction, stopTimeout, ct);
                return Results.Ok(new StackCommandResponse(result.Succeeded, result.Error, result.Output));
            })
            .Produces<StackCommandResponse>();

        var deployments = app.MapGroup("/api/dev").WithTags("Dev: deployments");

        deployments.MapGet("/stacks/{id:guid}/deployments", async (Guid id, DeploymentService service, CancellationToken ct) =>
            (await service.ListAsync(StackId.From(id), ct)).Select(d => new DeploymentResponse(
                d.Id.Value, d.Status, d.Trigger, d.PullPolicy, d.Commit?.Value, d.QueuedAt, d.StartedAt, d.FinishedAt, d.Error)));

        deployments.MapPost(
            "/stacks/{id:guid}/deployments",
            async (Guid id, DeployRequest request, DeploymentService service, CancellationToken ct) =>
            {
                var deployment = await service.QueueAsync(
                    StackId.From(id), request.Pull ? ImagePullPolicy.Always : ImagePullPolicy.Missing, ct);
                return TypedResults.Accepted($"/api/dev/deployments/{deployment.Id}", ToDetails(deployment));
            });

        deployments.MapGet("/deployments/{id:guid}", async (Guid id, DeploymentService service, CancellationToken ct) =>
            ToDetails(await service.GetAsync(DeploymentId.From(id), ct)));

        return app;
    }

    private static RepositoryResponse ToResponse(GitRepository repository) =>
        new(repository.Id.Value, repository.Name, repository.Url.ToString(), repository.CreatedAt);

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
        d.Log.Select(l => new DeploymentLogLineResponse(l.At, l.Level, l.Text)).ToList());

    public sealed record CreateRepositoryRequest(string Name, string Url);

    public sealed record RepositoryResponse(Guid Id, string Name, string Url, DateTimeOffset CreatedAt);

    /// <param name="ComposePath">Path of the compose file in the repository; its directory holds the stack's files.</param>
    public sealed record CreateStackRequest(string Name, Guid RepositoryId, string Branch, string ComposePath);

    public sealed record StackResponse(
        Guid Id,
        string Name,
        Guid RepositoryId,
        string Branch,
        string ComposePath,
        bool IsSystem,
        string? DeployedCommit,
        DateTimeOffset? DeployedAt,
        bool CanDeploy,
        bool CanRestart);

    /// <param name="Error">Why compose failed, or null when it succeeded.</param>
    /// <param name="Output">The last lines of compose output.</param>
    public sealed record StackCommandResponse(bool Succeeded, string? Error, IReadOnlyList<string> Output);

    /// <param name="Pull">True for "redeploy with pull": pull every image, not only missing ones.</param>
    public sealed record DeployRequest(bool Pull);

    public sealed record DeploymentResponse(
        Guid Id,
        DeploymentStatus Status,
        DeploymentTrigger Trigger,
        ImagePullPolicy PullPolicy,
        string? Commit,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        string? Error);

    public sealed record DeploymentDetailsResponse(
        Guid Id,
        Guid StackId,
        DeploymentStatus Status,
        DeploymentTrigger Trigger,
        ImagePullPolicy PullPolicy,
        string? Commit,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        string? Error,
        IReadOnlyList<DeploymentLogLineResponse> Log);

    public sealed record DeploymentLogLineResponse(DateTimeOffset At, DeploymentLogLevel Level, string Text);
}
