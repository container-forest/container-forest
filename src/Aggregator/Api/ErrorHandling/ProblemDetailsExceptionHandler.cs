using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Webhooks;
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
            PasswordRejectedException => (StatusCodes.Status400BadRequest, "Password rejected"),
            DomainConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            UnauthenticatedException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            AccessDeniedException => (StatusCodes.Status403Forbidden, "Forbidden"),
            AgentOfflineException or AgentDisconnectedException => (StatusCodes.Status503ServiceUnavailable, "Agent unavailable"),
            AgentTimeoutException => (StatusCodes.Status504GatewayTimeout, "Agent timeout"),
            WebhookRejectedException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            WebhookPayloadException => (StatusCodes.Status400BadRequest, "Invalid payload"),
            AgentCommandFailedException failed => (StatusFor(failed.Kind), "Agent command failed"),

            // A body or parameter that does not bind (e.g. an object where an id is expected) is the caller's error, not ours.
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Bad request"),
            _ => (0, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        };
        if (exception is PasswordRejectedException rejected)
        {
            context.ProblemDetails.Extensions["errors"] = rejected.Reasons;
        }

        return await problemDetails.TryWriteAsync(context);
    }

    private static int StatusFor(AgentErrorKind kind) => kind switch
    {
        AgentErrorKind.InvalidArgument => StatusCodes.Status400BadRequest,
        AgentErrorKind.NotFound => StatusCodes.Status404NotFound,
        AgentErrorKind.Busy => StatusCodes.Status409Conflict,
        AgentErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
        AgentErrorKind.Unsupported => StatusCodes.Status501NotImplemented,
        _ => StatusCodes.Status502BadGateway,
    };
}
