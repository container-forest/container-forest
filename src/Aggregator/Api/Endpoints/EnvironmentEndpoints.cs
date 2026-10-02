using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Environments and containers for the panel (see EnvironmentContracts).</summary>
internal static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/environments")
            .WithTags("Environments")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", async (EnvironmentService environments, CancellationToken ct) =>
                (await environments.ListVisibleAsync(ct))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new EnvironmentResponse(
                        e.Id.Value, e.Name, e.IsPrivate, e.IsEnrolled, e.IsOnline, e.AgentHostname, e.AgentVersion, e.LastSeenAt, e.Role))
                    .ToList())
            .WithName("ListEnvironments");

        group.MapGet("/{id:guid}/containers", async (Guid id, ContainerQueryService containers, CancellationToken ct) =>
                (await containers.ListAsync(EnvironmentId.From(id), ct))
                    .Select(v => new ContainerResponse(
                        v.Container.Id,
                        v.Container.Name,
                        v.Container.State,
                        v.Container.Image,
                        v.Container.StackName,
                        v.Container.ServiceName,
                        v.Container.StatusText,
                        v.Container.CreatedAt,
                        v.CanControl))
                    .ToList())
            .WithName("ListContainers")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithAgentProblems();

        group.MapGet(
                "/{id:guid}/containers/{containerId}/logs",
                async (Guid id, string containerId, int? tail, DateTimeOffset? since, ContainerLogService logs, CancellationToken ct) =>
                {
                    var lines = await logs.ReadAsync(EnvironmentId.From(id), containerId, tail, since, ct);
                    return new ContainerLogsResponse(lines.Select(l => new ContainerLogLineResponse(l.At, l.Stream, l.Text)).ToList());
                })
            .WithName("GetContainerLogs")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithAgentProblems();

        group.MapContainerAction("start", "StartContainer", ContainerAction.Start, hasStopTimeout: false);
        group.MapContainerAction("stop", "StopContainer", ContainerAction.Stop, hasStopTimeout: true);
        group.MapContainerAction("restart", "RestartContainer", ContainerAction.Restart, hasStopTimeout: true);

        return app;
    }

    /// <summary>
    /// One endpoint per action, so the OpenAPI document (and the panel's typed client) shows which actions take a
    /// stop timeout instead of a free-form action name.
    /// </summary>
    private static void MapContainerAction(
        this RouteGroupBuilder group, string verb, string name, ContainerAction action, bool hasStopTimeout)
    {
        var route = $"/{{id:guid}}/containers/{{containerId}}/{verb}";
        var endpoint = hasStopTimeout
            ? group.MapPost(route, (Guid id, string containerId, int? timeoutSeconds, ContainerCommandService commands, CancellationToken ct) =>
                RunAsync(commands, id, containerId, action, timeoutSeconds, ct))
            : group.MapPost(route, (Guid id, string containerId, ContainerCommandService commands, CancellationToken ct) =>
                RunAsync(commands, id, containerId, action, timeoutSeconds: null, ct));

        endpoint
            .WithName(name)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithAgentProblems();
    }

    private static async Task<IResult> RunAsync(
        ContainerCommandService commands, Guid id, string containerId, ContainerAction action, int? timeoutSeconds, CancellationToken ct)
    {
        var stopTimeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        await commands.ExecuteAsync(EnvironmentId.From(id), containerId, action, stopTimeout, ct);
        return TypedResults.NoContent();
    }

    /// <summary>The agent is offline (503), did not answer (504) or reported an error (502).</summary>
    private static RouteHandlerBuilder WithAgentProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
}
