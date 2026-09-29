using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace ContainerForest.Aggregator.Api.ErrorHandling;

/// <summary>Turns known domain and application exceptions into RFC 9457 problem responses.</summary>
internal sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DomainValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
            DomainConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            AgentOfflineException or AgentDisconnectedException => (StatusCodes.Status503ServiceUnavailable, "Agent unavailable"),
            AgentTimeoutException => (StatusCodes.Status504GatewayTimeout, "Agent timeout"),
            AgentCommandFailedException failed => (StatusFor(failed.Kind), "Agent command failed"),
            _ => (0, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        });
    }

    private static int StatusFor(AgentErrorKind kind) => kind switch
    {
        AgentErrorKind.InvalidArgument => StatusCodes.Status400BadRequest,
        AgentErrorKind.NotFound => StatusCodes.Status404NotFound,
        AgentErrorKind.Busy => StatusCodes.Status409Conflict,
        AgentErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway,
    };
}
