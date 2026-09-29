using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Temporary endpoints for local development until the panel API exists. They act as the user from the
/// X-Dev-User header (see DevUserAuthenticationHandler) and are mapped only in the Development environment.
/// </summary>
internal static class DevEnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapDevEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev/environments").WithTags("Dev: environments");

        group.MapGet("/", async (EnvironmentService environments, CancellationToken ct) =>
            (await environments.ListVisibleAsync(ct)).Select(e => new EnvironmentResponse(
                e.Id.Value, e.Name, e.IsPrivate, e.IsEnrolled, e.IsOnline, e.AgentHostname, e.AgentVersion, e.LastSeenAt, e.Role)));

        group.MapPost("/", async (CreateEnvironmentRequest request, EnvironmentService environments, CancellationToken ct) =>
        {
            var created = await environments.CreateAsync(request.Name, request.IsPrivate, ct);
            return TypedResults.Created(
                $"/api/dev/environments/{created.Id}",
                new EnvironmentCreatedResponse(
                    created.Id.Value, created.Name, created.EnrollmentToken.Token, created.EnrollmentToken.ExpiresAt));
        });

        group.MapPost("/{id:guid}/enrollment-token", async (Guid id, EnvironmentService environments, CancellationToken ct) =>
        {
            var token = await environments.IssueEnrollmentTokenAsync(EnvironmentId.From(id), ct);
            return new EnrollmentTokenResponse(token.Token, token.ExpiresAt);
        });

        group.MapGet("/{id:guid}/containers", async (Guid id, ContainerQueryService containers, CancellationToken ct) =>
            (await containers.ListAsync(EnvironmentId.From(id), ct)).Select(v => new ContainerResponse(
                v.Container.Id,
                v.Container.Name,
                v.Container.State,
                v.Container.Image,
                v.Container.StackName,
                v.Container.ServiceName,
                v.Container.StatusText,
                v.Container.CreatedAt,
                v.CanControl)));

        group.MapPost(
            "/{id:guid}/containers/{containerId}/{action}",
            async (Guid id, string containerId, string action, int? timeoutSeconds, ContainerCommandService commands, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var containerAction)
                    || !Enum.IsDefined(containerAction))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest, detail: "Action must be start, stop or restart.");
                }

                var stopTimeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
                await commands.ExecuteAsync(EnvironmentId.From(id), containerId, containerAction, stopTimeout, ct);
                return Results.NoContent();
            });

        return app;
    }

    public sealed record CreateEnvironmentRequest(string Name, bool IsPrivate);

    public sealed record EnvironmentCreatedResponse(Guid Id, string Name, string EnrollmentToken, DateTimeOffset ExpiresAt);

    public sealed record EnrollmentTokenResponse(string EnrollmentToken, DateTimeOffset ExpiresAt);

    public sealed record EnvironmentResponse(
        Guid Id,
        string Name,
        bool IsPrivate,
        bool IsEnrolled,
        bool IsOnline,
        string? AgentHostname,
        string? AgentVersion,
        DateTimeOffset? LastSeenAt,
        Role? Role);

    public sealed record ContainerResponse(
        string Id,
        string Name,
        ContainerState State,
        string Image,
        string? StackName,
        string? ServiceName,
        string StatusText,
        DateTimeOffset? CreatedAt,
        bool CanControl);
}
