using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Notifications;
using Discord;
using Discord.Interactions;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>
/// The buttons under notifications as Discord sees them. A notification is a channel message everyone there sees,
/// so a click runs as the user who clicked, with their own permissions, and only that user sees the answer. A restart
/// that worked is noted on the notification itself, so others in the channel do not restart the container again.
/// </summary>
public sealed partial class NotificationModule(
    RestartContainerCommand restart,
    ContainerLogsCommand logs,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<NotificationModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    public const string OutdatedButtonMessage =
        "❌ This button is from an older version of the bot. Use the slash commands or the panel instead.";

    /// <param name="rest">What the wildcard matched: the custom id after the prefix.</param>
    [ComponentInteraction(ContainerButton.Prefix + ":*", ignoreGroupNames: true)]
    public async Task ClickAsync(string rest)
    {
        if (!ContainerButton.TryParse($"{ContainerButton.Prefix}:{rest}", out var button))
        {
            await RespondAsync(OutdatedButtonMessage, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // The environment by id: the resolver finds it among the environments the clicker can see, or says it is not there.
        var interaction = new DiscordInteraction(Context.Interaction);
        var container = new ContainerActionArgs(button.EnvironmentId.ToString(), button.ContainerName);
        if (button.Action == ContainerButtonAction.Logs)
        {
            await logs.ExecuteAsync(interaction, container, lifetime.ApplicationStopping);
            return;
        }

        var reply = await restart.ExecuteAsync(interaction, container, lifetime.ApplicationStopping);
        if (!reply.IsError && Context.Interaction is IComponentInteraction click)
        {
            await NoteRestartAsync(click.Message, Context.User.Id);
        }
    }

    // The restart is done and its answer sent: a note that fails (the message was deleted, Discord is down) only goes to the log.
    private async Task NoteRestartAsync(IUserMessage notification, ulong discordUserId)
    {
        if (notification.Embeds.FirstOrDefault() is not { } embed)
        {
            return;
        }

        try
        {
            await notification.ModifyAsync(
                message =>
                {
                    message.Embeds = new[] { NotificationEmbed.WithRestart(embed, discordUserId, time.GetUtcNow()) };
                    message.AllowedMentions = AllowedMentions.None;
                },
                new RequestOptions { CancelToken = lifetime.ApplicationStopping });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNoteFailed(ex, notification.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not note the restart on notification message {MessageId}")]
    private partial void LogNoteFailed(Exception exception, ulong messageId);
}
