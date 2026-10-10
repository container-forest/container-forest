using System.Security.Claims;
using System.Threading.RateLimiting;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.AspNetCore.RateLimiting;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Decrypting and encrypting SOPS files for the git filter of the CLI, see SopsContracts.</summary>
internal static class SopsEndpoints
{
    private const string Policy = "sops";

    /// <summary>
    /// Per user: a checkout of a repository with many secret files decrypts each of them, but a leaked token should not
    /// turn the aggregator into a fast decryption service.
    /// </summary>
    /// <remarks>Rejections are 429: <see cref="CliAuthorizationEndpoints.AddCliAuthorizationRateLimits"/> sets it for all policies.</remarks>
    public static RateLimiterOptions AddSopsRateLimit(this RateLimiterOptions options)
    {
        options.AddPolicy(Policy, context => RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            }));
        return options;
    }

    public static IEndpointRouteBuilder MapSopsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sops")
            .WithTags("SOPS")
            .RequireRateLimiting(Policy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/decrypt", async (SopsDecryptRequest request, SopsSecretsService service, CancellationToken ct) =>
                new SopsContentResponse(await service.DecryptAsync(StackFilePath.Parse(request.Path), request.Content, ct)))
            .WithName("DecryptSopsFile");

        group.MapPost("/encrypt", async (SopsEncryptRequest request, SopsSecretsService service, CancellationToken ct) =>
                new SopsContentResponse(
                    await service.EncryptAsync(StackFilePath.Parse(request.Path), request.Content, request.SopsConfig, ct)))
            .WithName("EncryptSopsFile");

        return app;
    }
}
