using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Api.Grpc;

/// <summary>Configuration section "BotGateway" (env: CF_BOTGATEWAY__TOKENHASHES__0, ...).</summary>
/// <remarks>
/// The bot's token is as powerful as an administrator's password, so it is set by whoever runs the server,
/// never issued by an endpoint. Several hashes allow rotating the token without downtime.
/// </remarks>
public sealed class BotGatewayOptions
{
    public const string SectionName = "BotGateway";

    /// <summary>SHA-256 hashes of accepted bot tokens (64 lowercase hex characters). Empty: the bot is disabled.</summary>
    public List<string> TokenHashes { get; set; } = [];

    internal bool IsValid() =>
        TokenHashes.All(h => h.Length == 64 && h.All(char.IsAsciiHexDigitLower));
}

/// <summary>
/// Guards <see cref="BotGatewayService"/>: checks the bot token before every call and turns application
/// exceptions into the status codes documented in bot.proto.
/// </summary>
public sealed class BotGatewayInterceptor(IOptions<BotGatewayOptions> options, ILogger<BotGatewayInterceptor> logger) : Interceptor
{
    public const string TokenHeader = "x-bot-token";
    public const string ReasonTrailer = "x-cf-reason";
    public const string InvalidBotToken = "invalid-bot-token";
    public const string AccountNotLinked = "account-not-linked";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var token = context.RequestHeaders.GetValue(TokenHeader);
        if (string.IsNullOrEmpty(token) || !options.Value.TokenHashes.Any(hash => SecretToken.Matches(token, hash)))
        {
            logger.LogWarning("Rejected a bot gateway call from {Peer}: missing or invalid bot token", context.Peer);
            throw Unauthenticated("Invalid bot token.", InvalidBotToken);
        }

        try
        {
            return await continuation(request, context);
        }
        catch (UnauthenticatedException)
        {
            // The linked user no longer exists.
            throw Unauthenticated("This Discord account is not linked.", AccountNotLinked);
        }
        catch (Exception ex) when (ToStatus(ex) is { } status)
        {
            throw new RpcException(status);
        }
    }

    public static RpcException Unauthenticated(string message, string reason) =>
        new(new Status(StatusCode.Unauthenticated, message), new Metadata { { ReasonTrailer, reason } });

    private static Status? ToStatus(Exception exception) => exception switch
    {
        DomainValidationException => new Status(StatusCode.InvalidArgument, exception.Message),
        DomainConflictException => new Status(StatusCode.FailedPrecondition, exception.Message),
        NotFoundException => new Status(StatusCode.NotFound, exception.Message),
        AccessDeniedException => new Status(StatusCode.PermissionDenied, exception.Message),
        AgentOfflineException or AgentDisconnectedException => new Status(StatusCode.Unavailable, exception.Message),
        AgentTimeoutException => new Status(StatusCode.DeadlineExceeded, exception.Message),
        AgentCommandFailedException failed => new Status(ToStatusCode(failed.Kind), exception.Message),
        _ => null,
    };

    private static StatusCode ToStatusCode(AgentErrorKind kind) => kind switch
    {
        AgentErrorKind.InvalidArgument => StatusCode.InvalidArgument,
        AgentErrorKind.NotFound => StatusCode.NotFound,
        AgentErrorKind.Busy => StatusCode.FailedPrecondition,
        AgentErrorKind.Timeout => StatusCode.DeadlineExceeded,
        AgentErrorKind.Unsupported => StatusCode.Unimplemented,
        _ => StatusCode.Internal,
    };
}
