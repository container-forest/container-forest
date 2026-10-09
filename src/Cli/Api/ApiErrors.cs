using System.Net;
using Microsoft.Kiota.Abstractions;
using GeneratedProblem = ContainerForest.Cli.Api.Generated.Models.ProblemDetails;

namespace ContainerForest.Cli.Api;

/// <summary>Turns the aggregator's answers and network failures into messages for the terminal and exit codes.</summary>
public static class ApiErrors
{
    public static CliException Translate(ServerProblemException exception, Uri server) =>
        Translate(exception.Status, exception.Detail ?? exception.Title, server);

    public static CliException Translate(ApiException exception, Uri server) =>
        Translate(exception.ResponseStatusCode, exception is GeneratedProblem problem ? problem.Detail ?? problem.Title : null, server);

    private static CliException Translate(int status, string? detail, Uri server) =>
        status switch
        {
            401 => new CliException(
                $"{server} did not accept the token: it is unknown, revoked or expired. Run 'forest login' again.",
                ExitCodes.Unauthenticated),
            403 => new CliException(detail ?? "Not allowed.", ExitCodes.Denied),
            404 => new CliException(detail is null ? "Not found." : $"Not found: {detail}", ExitCodes.Denied),
            429 => new CliException("Too many requests; wait a minute and try again."),
            503 => new CliException(detail ?? "The agent or the server is unavailable.", ExitCodes.Unreachable),
            _ => new CliException(detail ?? $"The server answered {status} ({(HttpStatusCode)status})."),
        };

    public static CliException Unreachable(HttpRequestException exception, Uri server) =>
        new($"Could not reach {server}: {exception.Message}", ExitCodes.Unreachable);
}
