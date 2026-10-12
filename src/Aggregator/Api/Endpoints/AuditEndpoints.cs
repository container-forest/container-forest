using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>The audit log for administrators (see AuditContracts).</summary>
internal static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (
                AuditService service,
                CancellationToken ct,
                AuditCategory? category = null,
                AuditAction? action = null,
                AuditOutcome? outcome = null,
                Guid? userId = null,
                Guid? environmentId = null,
                string? search = null,
                DateTimeOffset? from = null,
                DateTimeOffset? to = null,
                int page = 1,
                int pageSize = AuditService.DefaultPageSize) =>
            {
                var filter = new AuditFilter(
                    category,
                    action,
                    outcome,
                    userId is { } user ? UserId.From(user) : null,
                    environmentId is { } environment ? EnvironmentId.From(environment) : null,
                    search,
                    from,
                    to);
                var result = await service.ListAsync(filter, page, pageSize, ct);
                return new AuditPageResponse(
                    result.Entries.Select(ToResponse).ToList(),
                    result.Total,
                    Math.Max(page, 1),
                    Math.Clamp(pageSize, 1, AuditService.MaxPageSize));
            })
            .WithTags("Audit")
            .WithName("ListAuditEntries")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static AuditEntryResponse ToResponse(AuditEntry entry) => new(
        entry.Id.Value,
        entry.OccurredAt,
        entry.Action,
        entry.Category,
        entry.Outcome,
        entry.ActorId?.Value,
        entry.ActorName,
        entry.Channel,
        entry.Address,
        entry.TargetKind,
        entry.TargetId,
        entry.TargetName,
        entry.EnvironmentId?.Value,
        entry.StackId?.Value,
        entry.Details);
}
