using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

/// <param name="Name">What the user sees in the list.</param>
/// <param name="Value">What Discord puts into the option when the user picks it.</param>
public sealed record Suggestion(string Name, string Value);

/// <summary>
/// Suggestions for environment and container options. Autocomplete cannot be deferred and must answer within
/// 3 seconds, so every lookup has a short budget and a failure means no suggestions, never an error.
/// Suggestions are only a convenience: the command checks whatever the user sends.
/// </summary>
public sealed class AutocompleteSource(IForestClient forest, ILogger<AutocompleteSource> logger)
{
    /// <summary>Discord shows at most 25 choices; names and values are at most 100 characters.</summary>
    public const int MaxSuggestions = 25;
    public const int MaxLength = 100;

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<Suggestion>> EnvironmentsAsync(BotUser user, string? input, CancellationToken cancellationToken)
    {
        var environments = await WithinBudgetAsync(ct => forest.ListEnvironmentsAsync(user.Id, ct), cancellationToken);
        return environments is null ? [] : SuggestEnvironments(environments, input);
    }

    /// <param name="environment">What the environment option holds so far: an id from autocomplete or a typed name.</param>
    /// <param name="action">The command being filled in; null shows every container the user may control.</param>
    public async Task<IReadOnlyList<Suggestion>> ContainersAsync(
        BotUser user, string? environment, ContainerAction? action, string? input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return [];
        }

        var containers = await WithinBudgetAsync(
            async ct =>
            {
                var environments = await forest.ListEnvironmentsAsync(user.Id, ct);
                var resolved = EnvironmentResolver.Find(environments, environment.Trim());
                return resolved is null ? [] : await forest.ListContainersAsync(user.Id, resolved.Id, ct);
            },
            cancellationToken);
        return containers is null ? [] : SuggestContainers(containers, action, input);
    }

    public static IReadOnlyList<Suggestion> SuggestEnvironments(IReadOnlyList<ForestEnvironment> environments, string? input) =>
    [
        .. environments
            .Where(e => Matches(e.Name, input))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(e => new Suggestion(Truncate(e.Online ? e.Name : $"{e.Name} (offline)"), e.Id)),
    ];

    /// <summary>
    /// Containers the user may control and the action makes sense for: /start offers stopped containers,
    /// /stop running ones. Hiding the rest is cosmetic; the aggregator decides.
    /// </summary>
    public static IReadOnlyList<Suggestion> SuggestContainers(
        IReadOnlyList<ForestContainer> containers, ContainerAction? action, string? input) =>
    [
        .. containers
            .Where(c => c.CanControl && Fits(action, c.State) && (Matches(c.Name, input) || IsIdPrefix(c.Id, input)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(c => new Suggestion(
                Truncate($"{c.Name} — {c.State.ToString().ToLowerInvariant()}"),
                // A name longer than an option value may hold is sent as the id prefix, which the aggregator accepts too.
                c.Name.Length <= MaxLength ? c.Name : c.Id[..Math.Min(12, c.Id.Length)])),
    ];

    private static bool Fits(ContainerAction? action, ContainerState state) => action switch
    {
        ContainerAction.Start => state is not (ContainerState.Running or ContainerState.Restarting),
        ContainerAction.Stop => state is ContainerState.Running or ContainerState.Restarting or ContainerState.Paused,
        _ => true,
    };

    private static bool Matches(string candidate, string? input) =>
        string.IsNullOrWhiteSpace(input) || candidate.Contains(input.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Ids are matched as in the docker CLI: by prefix, not anywhere inside.</summary>
    private static bool IsIdPrefix(string id, string? input) =>
        !string.IsNullOrWhiteSpace(input) && id.StartsWith(input.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";

    private async Task<T?> WithinBudgetAsync<T>(Func<CancellationToken, Task<T>> lookup, CancellationToken cancellationToken)
        where T : class
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            return await lookup(budget.Token);
        }
        catch (ForestException ex)
        {
            // An unlinked account or an offline agent: the command itself will tell the user.
            logger.LogDebug("No autocomplete suggestions: {Reason}", ex.Message);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("No autocomplete suggestions: the aggregator did not answer within {Budget}", Budget);
            return null;
        }
    }
}
