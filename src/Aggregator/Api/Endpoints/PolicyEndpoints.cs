using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Policies;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Policies;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>The compose policy of an environment (see PolicyContracts).</summary>
internal static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/environments/{id:guid}/compose-policy")
            .WithTags("Compose policy")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", async (Guid id, ComposePolicyService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(EnvironmentId.From(id), ct)))
            .WithName("GetComposePolicy");

        group.MapPut("/", async (Guid id, UpdateComposePolicyRequest request, ComposePolicyService service, CancellationToken ct) =>
                ToResponse(await service.ChangeAsync(
                    EnvironmentId.From(id), request.Mode, request.DataRoot, request.SystemReadOnlyPaths, request.Allowances, ct)))
            .WithName("UpdateComposePolicy")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/report", async (Guid id, ComposePolicyService service, CancellationToken ct) =>
                (await service.ReportAsync(EnvironmentId.From(id), ct)).Select(r => new StackPolicyReportResponse(
                    r.StackId.Value,
                    r.StackName.Value,
                    r.Checked,
                    r.Violations.Select(v => new PolicyViolationResponse(v.Service, v.Message, v.Allowance?.Value, v.StackOnly)).ToList(),
                    r.Warnings,
                    r.Allowances,
                    r.Unrestricted)).ToList())
            .WithName("GetComposePolicyReport");

        return app;
    }

    private static ComposePolicyResponse ToResponse(ComposePolicyView view) => new(
        view.Policy.Mode,
        view.Policy.DataRoot,
        view.Policy.SystemReadOnlyPaths,
        view.Policy.Allowances,
        ComposePolicy.ProtectedPaths,
        view.CanChange);
}
