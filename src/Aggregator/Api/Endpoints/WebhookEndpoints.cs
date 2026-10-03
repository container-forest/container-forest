using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Webhooks;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Push webhooks from git servers (see WebhookContracts).</summary>
internal static class WebhookEndpoints
{
    /// <summary>GitHub caps deliveries at 25 MB, but a push large enough to exceed this is no reason to read it all.</summary>
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/{repositoryId:guid}", ReceiveAsync)
            .WithTags("Webhooks")
            .WithName("ReceiveWebhook")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return app;
    }

    private static async Task<WebhookResponse> ReceiveAsync(
        Guid repositoryId, HttpRequest request, PushWebhookService service, CancellationToken ct)
    {
        // The signature covers the exact bytes, so the body is read raw rather than bound as JSON.
        var body = await ReadBodyAsync(request, ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var result = await service.ReceiveAsync(RepositoryId.From(repositoryId), new WebhookDelivery(headers, body), ct);
        return new WebhookResponse(
            result.Provider,
            result.Outcome,
            result.Detail,
            result.Stacks.Select(s => new WebhookStackResponse(
                s.StackId.Value, s.EnvironmentId.Value, s.Name.Value, s.Outcome, s.DeploymentId?.Value)).ToList());
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            throw new BadHttpRequestException("The webhook body is too large.", StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new BadHttpRequestException("The webhook body is too large.", StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
