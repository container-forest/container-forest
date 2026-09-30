using Grpc.Core;

namespace ContainerForest.DiscordBot.Forest;

/// <summary>Translates the aggregator's gRPC status codes (see the header of bot.proto) into <see cref="ForestException"/>.</summary>
public static class RpcErrorMapper
{
    /// <summary>For Unauthenticated, the trailer that tells a bad bot token from an unlinked Discord account.</summary>
    public const string ReasonTrailer = "x-cf-reason";
    public const string InvalidBotTokenReason = "invalid-bot-token";
    public const string AccountNotLinkedReason = "account-not-linked";

    public static ForestException ToForestException(RpcException ex)
    {
        // The client sets a debug exception only when it made the status up itself: the call never got an answer
        // from the aggregator (refused connection, TLS or protocol error). Unavailable or Internal alike.
        if (ex.Status.DebugException is { } cause && ex.StatusCode != StatusCode.DeadlineExceeded)
        {
            return new ForestUnavailableException($"The aggregator is unreachable: {cause.Message}", ex);
        }

        var detail = ex.Status.Detail;
        return ex.StatusCode switch
        {
            StatusCode.Unauthenticated => ex.Trailers.GetValue(ReasonTrailer) switch
            {
                AccountNotLinkedReason => new AccountNotLinkedException(ex),
                InvalidBotTokenReason => new ForestUnavailableException(
                    "The aggregator rejected the bot's service token. Check that the SHA-256 hash of CF_BOT__SERVICETOKEN "
                    + "is in the aggregator's BotGateway:TokenHashes.",
                    ex),
                var reason => new ForestUnavailableException(
                    $"The aggregator rejected the call as unauthenticated without a known reason ('{reason}').", ex),
            },
            StatusCode.PermissionDenied => new ForestAccessDeniedException(detail, ex),
            StatusCode.NotFound => new ForestNotFoundException(detail, ex),
            StatusCode.InvalidArgument or StatusCode.FailedPrecondition => new ForestRequestRejectedException(detail, ex),
            StatusCode.Unavailable => new AgentUnavailableException(detail, ex),

            // Either the agent or, with the client's own deadline, the aggregator did not answer. The action may
            // still happen, so the user should look before retrying.
            StatusCode.DeadlineExceeded => new AgentUnavailableException(
                string.IsNullOrWhiteSpace(detail)
                    ? "No answer in time. The action may still complete: check the container before retrying."
                    : detail,
                ex),

            StatusCode.Unimplemented or StatusCode.Internal => new AgentFailedException(detail, ex),
            _ => new ForestUnavailableException($"Unexpected answer from the aggregator: {ex.StatusCode} '{detail}'.", ex),
        };
    }
}
