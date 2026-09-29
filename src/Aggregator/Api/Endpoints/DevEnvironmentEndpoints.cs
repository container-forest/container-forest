using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Temporary endpoints for local development until login and the real panel API exist.
/// They have no authentication, so Program.cs maps them only in the Development environment.
/// </summary>
internal static class DevEnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapDevEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev/environments").WithTags("Dev: environments");

        group.MapGet("/", async (EnvironmentService environments, CancellationToken ct) =>
            (await environments.ListAsync(ct)).Select(e => new EnvironmentResponse(
                e.Id.Value, e.Name, e.IsPrivate, e.IsEnrolled, e.IsOnline, e.AgentHostname, e.AgentVersion, e.LastSeenAt)));

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

        group.MapGet("/{id:guid}/containers", (Guid id, IAgentChannel agents, CancellationToken ct) =>
            agents.ListContainersAsync(EnvironmentId.From(id), ct));

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
        DateTimeOffset? LastSeenAt);
}
