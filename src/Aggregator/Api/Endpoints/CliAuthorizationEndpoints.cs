using System.Threading.RateLimiting;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Signing the CLI in through the browser, see CliAuthorizationContracts.</summary>
internal static class CliAuthorizationEndpoints
{
    private const string StartPolicy = "cli-start";
    private const string PollPolicy = "cli-poll";
    private const string CodePolicy = "cli-code";

    private const string VerificationPath = "/cli/device";

    /// <summary>
    /// Limits per client address. Starting is rare; polling happens every few seconds; user codes are short, so the
    /// endpoints that take one must not allow guessing.
    /// </summary>
    public static IServiceCollection AddCliAuthorizationRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(StartPolicy, context => PerAddress(context, permits: 10));
            options.AddPolicy(PollPolicy, context => PerAddress(context, permits: 30));
            options.AddPolicy(CodePolicy, context => PerAddress(context, permits: 20));
        });

    public static IEndpointRouteBuilder MapCliAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cli").WithTags("CLI");

        group.MapPost("/authorizations", async (
                StartCliAuthorizationRequest request, HttpContext http, CliAuthorizationService service, CancellationToken ct) =>
            {
                var started = await service.StartAsync(
                    request.ClientName, request.HostName, http.Connection.RemoteIpAddress?.ToString(), ct, request.Secrets);
                return new CliAuthorizationStartedResponse(
                    started.DeviceCode,
                    started.UserCode,
                    VerificationPath,
                    $"/cli/authorize?code={Uri.EscapeDataString(started.UserCode)}",
                    started.Authorization.ExpiresAt,
                    (int)CliAuthorization.PollInterval.TotalSeconds);
            })
            .WithName("StartCliAuthorization")
            .RequireRateLimiting(StartPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/token", async (CliTokenRequest request, CliAuthorizationService service, TimeProvider time, CancellationToken ct) =>
            {
                var result = await service.ExchangeAsync(request.DeviceCode, ct);
                return new CliTokenResponse(
                    result.Status,
                    result.Secret,
                    result.Token is { } token ? ApiTokenEndpoints.ToResponse(token, time.GetUtcNow()) : null,
                    result.UserName);
            })
            .WithName("ExchangeCliToken")
            .RequireRateLimiting(PollPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var codes = group.MapGroup("/authorizations/{userCode}")
            .RequireRateLimiting(CodePolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        codes.MapGet("/", async (string userCode, CliAuthorizationService service, CancellationToken ct) =>
            {
                var authorization = await service.GetOpenAsync(userCode, ct);
                return new CliAuthorizationResponse(
                    CliAuthorization.FormatUserCode(userCode),
                    authorization.ClientName,
                    authorization.HostName,
                    authorization.RequestedFrom,
                    authorization.CreatedAt,
                    authorization.ExpiresAt,
                    authorization.SecretsRequested);
            })
            .WithName("GetCliAuthorization");

        codes.MapPost("/approve", async (
                string userCode, ApproveCliAuthorizationRequest request, CliAuthorizationService service, CancellationToken ct) =>
            {
                await service.ApproveAsync(
                    userCode,
                    request.Name,
                    request.ReadOnly,
                    request.EnvironmentIds?.Select(EnvironmentId.From).ToList(),
                    request.ExpiresAt,
                    ct,
                    request.Secrets);
                return TypedResults.NoContent();
            })
            .WithName("ApproveCliAuthorization")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        codes.MapPost("/deny", async (string userCode, CliAuthorizationService service, CancellationToken ct) =>
            {
                await service.DenyAsync(userCode, ct);
                return TypedResults.NoContent();
            })
            .WithName("DenyCliAuthorization");

        return app;
    }

    private static RateLimitPartition<string> PerAddress(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
}
