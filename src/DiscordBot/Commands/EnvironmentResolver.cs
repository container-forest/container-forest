using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

/// <summary>
/// Finds the environment a user meant. Autocomplete fills in the id, but a user may also type the name.
/// Only environments the aggregator lists for the user are candidates, so this reveals nothing new.
/// </summary>
public sealed class EnvironmentResolver(IForestClient forest)
{
    public async Task<ForestEnvironment> ResolveAsync(BotUser user, string input, CancellationToken cancellationToken)
    {
        var text = input.Trim();
        var environments = await forest.ListEnvironmentsAsync(user.Id, cancellationToken);
        return Find(environments, text)
            ?? throw new ForestNotFoundException($"Environment '{text}' was not found among the environments you can see.");
    }

    internal static ForestEnvironment? Find(IReadOnlyList<ForestEnvironment> environments, string text)
    {
        var byId = environments.FirstOrDefault(e => string.Equals(e.Id, text, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        var byExactName = environments.FirstOrDefault(e => e.Name == text);
        if (byExactName is not null)
        {
            return byExactName;
        }

        var byName = environments.Where(e => string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }
}
