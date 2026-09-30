namespace ContainerForest.DiscordBot.Forest;

/// <summary>
/// An expected failure of a call to the aggregator. <see cref="Exception.Message"/> is shown to the Discord user,
/// so it is readable text without exception details or secrets.
/// </summary>
public abstract class ForestException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The aggregator's status message when it sent one; it is readable by contract.</summary>
    protected static string DetailOr(string? detail, string fallback) =>
        string.IsNullOrWhiteSpace(detail) ? fallback : detail;
}

/// <summary>The Discord account is not linked to a user of the system.</summary>
public sealed class AccountNotLinkedException(Exception? innerException = null)
    : ForestException(
        "Your Discord account is not linked to container-forest. Get a code in the panel and link it with /link.",
        innerException);

/// <summary>The user lacks the role for the action or is deactivated.</summary>
public sealed class ForestAccessDeniedException(string? detail, Exception? innerException = null)
    : ForestException(DetailOr(detail, "You do not have permission to do that."), innerException);

/// <summary>Unknown environment or container, or one the user may not see.</summary>
public sealed class ForestNotFoundException(string? detail, Exception? innerException = null)
    : ForestException(DetailOr(detail, "Not found."), innerException);

/// <summary>A malformed request, a wrong link code, or an action that makes no sense in the current state.</summary>
public sealed class ForestRequestRejectedException(string? detail, Exception? innerException = null)
    : ForestException(DetailOr(detail, "The request was rejected."), innerException);

/// <summary>The environment's agent is offline or did not answer in time; nothing was queued.</summary>
public sealed class AgentUnavailableException(string? detail, Exception? innerException = null)
    : ForestException(DetailOr(detail, "The environment's agent is not available."), innerException);

/// <summary>The agent reported an error or does not support the command.</summary>
public sealed class AgentFailedException(string? detail, Exception? innerException = null)
    : ForestException(DetailOr(detail, "The agent could not do that."), innerException);

/// <summary>
/// The bot cannot talk to the aggregator: it is unreachable, rejects the bot's token, or answered unexpectedly.
/// This is the operator's problem, not the user's, so the user sees a generic message and
/// <see cref="Diagnostic"/> goes to the log.
/// </summary>
public sealed class ForestUnavailableException(string diagnostic, Exception? innerException = null)
    : ForestException("The bot is temporarily unavailable. Try again later.", innerException)
{
    /// <summary>What went wrong, for the log. Never contains the bot token.</summary>
    public string Diagnostic { get; } = diagnostic;
}
