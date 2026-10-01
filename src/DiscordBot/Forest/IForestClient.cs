namespace ContainerForest.DiscordBot.Forest;

/// <summary>
/// The aggregator as the bot's commands see it. Every call names the Discord user the bot acts for; the
/// aggregator checks that user's permissions, the bot decides nothing itself (security invariant 4).
/// </summary>
/// <remarks>
/// Failures are <see cref="ForestException"/> subclasses whose message may be shown to the user as it is.
/// Cancellation through the token is <see cref="OperationCanceledException"/>.
/// </remarks>
public interface IForestClient
{
    /// <summary>The linked user behind the Discord account, or null if the account is not linked.</summary>
    Task<ForestCaller?> GetCallerAsync(ulong discordUserId, CancellationToken cancellationToken);

    /// <summary>Links the Discord account with a one-time code from the panel; returns the linked user's name.</summary>
    Task<string> LinkAccountAsync(ulong discordUserId, string discordUserName, string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<ForestEnvironment>> ListEnvironmentsAsync(ulong discordUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ForestContainer>> ListContainersAsync(
        ulong discordUserId, string environmentId, CancellationToken cancellationToken);

    /// <summary>Returns once the agent has done it.</summary>
    /// <param name="container">A full id, an exact name or a unique id prefix, as in the docker CLI.</param>
    /// <param name="timeoutSeconds">For stop and restart: 0 kills at once; null means the aggregator's default.</param>
    Task ControlContainerAsync(
        ulong discordUserId,
        string environmentId,
        string container,
        ContainerAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ForestStack>> ListStacksAsync(ulong discordUserId, string environmentId, CancellationToken cancellationToken);

    /// <summary>Returns once docker compose has finished; a failed compose run is a result, not an exception.</summary>
    /// <param name="stack">The stack's name in the environment.</param>
    /// <param name="timeoutSeconds">Per container: 0 kills at once; null means the aggregator's default.</param>
    Task<ForestStackResult> ControlStackAsync(
        ulong discordUserId,
        string environmentId,
        string stack,
        StackAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken);
}
