using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Dashboard;
using Discord;
using Discord.Interactions;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>
/// /dashboard and its buttons as Discord sees them. The menu is one ephemeral message, seen only by the user who
/// opened it; every click edits it in place and runs as the user who clicked. The logic is in
/// <see cref="DashboardService"/>, the drawing in <see cref="DashboardRenderer"/>.
/// </summary>
public sealed class DashboardModule(DashboardService dashboard, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("dashboard", "Browse environments and stacks; redeploy, restart or take down a stack")]
    public async Task OpenAsync()
    {
        // The aggregator may take longer than Discord's 3 seconds; a deferred reply keeps the visibility it was deferred with.
        await DeferAsync(ephemeral: true, Options);
        await ShowAsync(new EnvironmentsRoute(0));
    }

    /// <param name="rest">What the wildcard matched: the custom id after the prefix.</param>
    [ComponentInteraction(DashboardRoute.Prefix + ":*", ignoreGroupNames: true)]
    public async Task NavigateAsync(string rest)
    {
        // For a button, deferring acknowledges the click and lets the bot edit the message it is on.
        await DeferAsync(ephemeral: true, Options);

        // An id this bot cannot read comes from an older version of the menu: start over rather than fail.
        var route = DashboardRoute.TryParse($"{DashboardRoute.Prefix}:{rest}", out var parsed) ? parsed : new EnvironmentsRoute(0);
        await ShowAsync(route);
    }

    private RequestOptions Options => new() { CancelToken = lifetime.ApplicationStopping };

    private async Task ShowAsync(DashboardRoute route)
    {
        var user = new BotUser(Context.User.Id, Context.User.Username);
        var screen = await dashboard.ShowAsync(user, route, DrawAsync, lifetime.ApplicationStopping);
        await DrawAsync(screen);
    }

    private Task DrawAsync(DashboardScreen screen) =>
        Context.Interaction.ModifyOriginalResponseAsync(
            message =>
            {
                message.Components = DashboardRenderer.Render(screen);
                message.Flags = MessageFlags.ComponentsV2;
                // Names come from users, the aggregator and Docker; none of them may ping anyone.
                message.AllowedMentions = AllowedMentions.None;
            },
            Options);
}
