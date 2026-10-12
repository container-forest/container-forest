using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Notifications;
using Discord;
using Discord.Interactions;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>
/// The buttons under notifications as Discord sees them. A notification is a channel message everyone there sees,
/// so a click runs as the user who clicked, with their own permissions, and only that user sees the answer; the
/// notification itself is not changed.
/// </summary>
public sealed class NotificationModule(RestartContainerCommand restart, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    public const string OutdatedButtonMessage = "❌ This button is from an older version of the bot. Use /restart instead.";

    /// <param name="rest">What the wildcard matched: the custom id after the prefix.</param>
    [ComponentInteraction(RestartContainerButton.Prefix + ":*", ignoreGroupNames: true)]
    public async Task RestartAsync(string rest)
    {
        if (!RestartContainerButton.TryParse($"{RestartContainerButton.Prefix}:{rest}", out var button))
        {
            await RespondAsync(OutdatedButtonMessage, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // The environment by id: the resolver finds it among the environments the clicker can see, or says it is not there.
        await restart.ExecuteAsync(
            new DiscordInteraction(Context.Interaction),
            new ContainerActionArgs(button.EnvironmentId.ToString(), button.ContainerName),
            lifetime.ApplicationStopping);
    }
}
