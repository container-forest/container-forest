using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Forest;
using Discord;
using Discord.Interactions;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>Suggests the environments the user can see.</summary>
public sealed class EnvironmentAutocompleteHandler(AutocompleteSource source) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var suggestions = await source.EnvironmentsAsync(
            new BotUser(context.User.Id, context.User.Username), autocompleteInteraction.Data.Current.Value as string, default);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Name, s.Value)));
    }
}

/// <summary>Suggests containers of the environment already chosen in the same command.</summary>
public sealed class ContainerAutocompleteHandler(AutocompleteSource source) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var data = autocompleteInteraction.Data;
        var environment = data.Options.FirstOrDefault(o => o.Name == "environment")?.Value as string;
        ContainerAction? action = data.CommandName switch
        {
            "start" => ContainerAction.Start,
            "stop" => ContainerAction.Stop,
            "restart" => ContainerAction.Restart,
            "remove" => ContainerAction.Remove,
            _ => null,
        };

        var suggestions = await source.ContainersAsync(
            new BotUser(context.User.Id, context.User.Username), environment, action, data.Current.Value as string, default);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Name, s.Value)));
    }
}

/// <summary>Suggests stacks of the environment already chosen in the same command that the user may act on.</summary>
public sealed class StackAutocompleteHandler(AutocompleteSource source) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var data = autocompleteInteraction.Data;
        var environment = data.Options.FirstOrDefault(o => o.Name == "environment")?.Value as string;
        StackAction? action = data.CommandName switch
        {
            "stack-restart" => StackAction.Restart,
            "stack-down" => StackAction.Down,
            "stack-up" => StackAction.Up,
            "stack-pull" => StackAction.Pull,
            _ => null,
        };

        var suggestions = await source.StacksAsync(
            new BotUser(context.User.Id, context.User.Username), environment, action, data.Current.Value as string, default);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Name, s.Value)));
    }
}
