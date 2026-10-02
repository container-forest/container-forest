using ContainerForest.DiscordBot.Commands;
using Discord.Interactions;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>
/// The slash commands as Discord sees them: names, options, descriptions. Each one only hands its options to the
/// matching <see cref="BotCommand{TArgs, TResult}"/>, which holds the logic.
/// </summary>
public sealed class ForestModule(
    EnvironmentsCommand environments,
    ContainersCommand containers,
    StartContainerCommand start,
    StopContainerCommand stop,
    RestartContainerCommand restart,
    StacksCommand stacks,
    RestartStackCommand restartStack,
    DownStackCommand downStack,
    LinkCommand link,
    IHostApplicationLifetime lifetime) : InteractionModuleBase<SocketInteractionContext>
{
    private const string EnvironmentDescription = "Environment name";
    private const string ContainerDescription = "Container name, id or unique id prefix";
    private const string TimeoutDescription = "Seconds to wait for a clean stop before killing; 0 kills at once";
    private const string StackDescription = "Stack name";
    private const string StackTimeoutDescription = "Seconds to wait for each container to stop cleanly before killing; 0 kills at once";

    [SlashCommand("environments", "Environments you can see, with their agent status")]
    public Task EnvironmentsAsync() =>
        environments.ExecuteAsync(Current, default, lifetime.ApplicationStopping);

    [SlashCommand("containers", "Containers of an environment")]
    public Task ContainersAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment) =>
        containers.ExecuteAsync(Current, environment, lifetime.ApplicationStopping);

    [SlashCommand("start", "Start a container")]
    public Task StartAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment,
        [Summary("container", ContainerDescription), Autocomplete(typeof(ContainerAutocompleteHandler))] string container) =>
        start.ExecuteAsync(Current, new ContainerActionArgs(environment, container), lifetime.ApplicationStopping);

    [SlashCommand("stop", "Stop a container")]
    public Task StopAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment,
        [Summary("container", ContainerDescription), Autocomplete(typeof(ContainerAutocompleteHandler))] string container,
        [Summary("timeout", TimeoutDescription), MinValue(0), MaxValue(300)] int? timeout = null) =>
        stop.ExecuteAsync(Current, new ContainerActionArgs(environment, container, timeout), lifetime.ApplicationStopping);

    [SlashCommand("restart", "Restart a container")]
    public Task RestartAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment,
        [Summary("container", ContainerDescription), Autocomplete(typeof(ContainerAutocompleteHandler))] string container,
        [Summary("timeout", TimeoutDescription), MinValue(0), MaxValue(300)] int? timeout = null) =>
        restart.ExecuteAsync(Current, new ContainerActionArgs(environment, container, timeout), lifetime.ApplicationStopping);

    [SlashCommand("stacks", "Stacks of an environment")]
    public Task StacksAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment) =>
        stacks.ExecuteAsync(Current, environment, lifetime.ApplicationStopping);

    [SlashCommand("stack-restart", "Restart every container of a stack (docker compose restart)")]
    public Task RestartStackAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment,
        [Summary("stack", StackDescription), Autocomplete(typeof(StackAutocompleteHandler))] string stack,
        [Summary("timeout", StackTimeoutDescription), MinValue(0), MaxValue(300)] int? timeout = null) =>
        restartStack.ExecuteAsync(Current, new StackActionArgs(environment, stack, timeout), lifetime.ApplicationStopping);

    [SlashCommand("stack-down", "Remove a stack's containers and networks; volumes and files stay (docker compose down)")]
    public Task DownStackAsync(
        [Summary("environment", EnvironmentDescription), Autocomplete(typeof(EnvironmentAutocompleteHandler))] string environment,
        [Summary("stack", StackDescription), Autocomplete(typeof(StackAutocompleteHandler))] string stack,
        [Summary("timeout", StackTimeoutDescription), MinValue(0), MaxValue(300)] int? timeout = null) =>
        downStack.ExecuteAsync(Current, new StackActionArgs(environment, stack, timeout), lifetime.ApplicationStopping);

    [SlashCommand("link", "Link your Discord account with a one-time code from the container-forest panel")]
    public Task LinkAsync([Summary("code", "The code shown in the panel")] string code) =>
        link.ExecuteAsync(Current, code, lifetime.ApplicationStopping);

    private DiscordInteraction Current => new(Context.Interaction);
}
