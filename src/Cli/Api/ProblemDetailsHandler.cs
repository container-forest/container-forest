using System.Text.Json;

namespace ContainerForest.Cli.Api;

/// <summary>
/// The aggregator answered with an error. Carries the problem details it sent (RFC 9457), whatever the status:
/// the generated client knows them only for the statuses the OpenAPI document lists.
/// </summary>
public sealed class ServerProblemException(int status, string? title, string? detail)
    : Exception(detail ?? title ?? $"The server answered {status}.")
{
    public int Status { get; } = status;

    public string? Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>Turns every unsuccessful answer into a <see cref="ServerProblemException"/> before the generated client sees it.</summary>
internal sealed class ProblemDetailsHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string? title = null;
            string? detail = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    title = Text(document.RootElement, "title");
                    detail = Text(document.RootElement, "detail");
                }
                catch (JsonException)
                {
                    // Not problem details after all; the status alone says enough.
                }
            }

            throw new ServerProblemException((int)response.StatusCode, title, detail);
        }
    }

    private static string? Text(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
