using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Registries;
using Microsoft.AspNetCore.Http.HttpResults;
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
                    .Select(ToResponse)
                    .ToList())
            .WithName("ListEnvironments");

        group.MapEnvironmentAdministration();
        group.MapRegistryCredentials();

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

    /// <summary>Creating and changing environments, their agents' enrollment and revocation. Administrators only.</summary>
    private static void MapEnvironmentAdministration(this RouteGroupBuilder group)
    {
        group.MapPost("/", async (CreateEnvironmentRequest request, EnvironmentService environments, CancellationToken ct) =>
            {
                var created = await environments.CreateAsync(request.Name, request.IsPrivate, ct);
                return TypedResults.Created(
                    $"/api/environments/{created.Id}",
                    new EnvironmentCreatedResponse(created.Id.Value, created.Name, created.EnrollmentToken.Token, created.EnrollmentToken.ExpiresAt));
            })
            .WithName("CreateEnvironment")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, UpdateEnvironmentRequest request, EnvironmentService environments, CancellationToken ct) =>
                ToResponse(await environments.UpdateAsync(EnvironmentId.From(id), request.Name, request.IsPrivate, ct)))
            .WithName("UpdateEnvironment")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/enrollment-token", async (Guid id, EnvironmentService environments, CancellationToken ct) =>
            {
                var token = await environments.IssueEnrollmentTokenAsync(EnvironmentId.From(id), ct);
                return new EnrollmentTokenResponse(token.Token, token.ExpiresAt);
            })
            .WithName("IssueEnrollmentToken")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/revoke-agent", async (Guid id, EnvironmentService environments, CancellationToken ct) =>
            {
                await environments.RevokeAgentAsync(EnvironmentId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeAgent")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Logins for private registries the environment's stacks pull from. Administrators only; passwords are never returned.</summary>
    private static void MapRegistryCredentials(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/registry-credentials", async (Guid id, RegistryCredentialService service, CancellationToken ct) =>
                (await service.ListAsync(EnvironmentId.From(id), ct)).Select(ToResponse).ToList())
            .WithName("ListRegistryCredentials")
            .ProducesProblem(StatusCodes.Status404NotFound);

        // PUT by server: setting the credentials of a registry again replaces them, e.g. after a token rotation.
        group.MapPut(
                "/{id:guid}/registry-credentials",
                async Task<Results<Created<RegistryCredentialResponse>, Ok<RegistryCredentialResponse>>> (
                    Guid id, SetRegistryCredentialRequest request, RegistryCredentialService service, CancellationToken ct) =>
                {
                    var (credential, created) = await service.SetAsync(
                        EnvironmentId.From(id), request.Server, request.Username, request.Password, ct);
                    var response = ToResponse(credential);
                    return created
                        ? TypedResults.Created($"/api/environments/{id}/registry-credentials/{credential.Id}", response)
                        : TypedResults.Ok(response);
                })
            .WithName("SetRegistryCredential")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete(
                "/{id:guid}/registry-credentials/{credentialId:guid}",
                async (Guid id, Guid credentialId, RegistryCredentialService service, CancellationToken ct) =>
                {
                    await service.DeleteAsync(EnvironmentId.From(id), RegistryCredentialId.From(credentialId), ct);
                    return TypedResults.NoContent();
                })
            .WithName("DeleteRegistryCredential")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static EnvironmentResponse ToResponse(EnvironmentSummary e) => new(
        e.Id.Value, e.Name, e.IsPrivate, e.IsEnrolled, e.IsOnline, e.AgentHostname, e.AgentVersion, e.LastSeenAt, e.Role, e.EnrollmentTokenExpiresAt);

    private static RegistryCredentialResponse ToResponse(RegistryCredentialSummary c) =>
        new(c.Id.Value, c.Server, c.Username, c.CreatedAt, c.UpdatedAt);

    /// <summary>The agent is offline (503), did not answer (504) or reported an error (502).</summary>
    private static RouteHandlerBuilder WithAgentProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
}
