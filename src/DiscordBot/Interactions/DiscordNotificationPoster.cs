using ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Notifications;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>Posts notifications as embeds in Discord channels. Discord's refusals become readable reasons for the panel.</summary>
public sealed class DiscordNotificationPoster : INotificationPoster
{
    // https://discord.com/developers/docs/topics/opcodes-and-status-codes#json-json-error-codes
    private const int UnknownChannel = 10003;
    private const int MissingAccess = 50001;
    private const int CannotSendInNonTextChannel = 50008;
    private const int MissingPermissions = 50013;

    private readonly DiscordSocketClient client;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DiscordNotificationPoster(DiscordSocketClient client)
    {
        this.client = client;

        // Constructed with the hosted services, before the bot signs in, so the first Ready is not missed.
        client.Ready += () =>
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    public async Task<DeliveryResult> PostAsync(ForestNotification notification, CancellationToken cancellationToken)
    {
        var channelId = notification.DiscordChannelId;
        var options = new RequestOptions { CancelToken = cancellationToken };
        try
        {
            var channel = await client.GetChannelAsync(channelId, options);
            if (channel is not IMessageChannel messageChannel)
            {
                return DeliveryResult.Failed(channel is null
                    ? $"Discord channel {channelId} does not exist or the bot is not on its server."
                    : $"Discord channel {channelId} is not a text channel.");
            }

            await messageChannel.SendMessageAsync(
                embed: NotificationEmbed.Build(notification),
                allowedMentions: AllowedMentions.None,
                components: NotificationEmbed.Buttons(notification),
                options: options);
            return DeliveryResult.Delivered;
        }
        catch (HttpException ex)
        {
            return DeliveryResult.Failed(Describe(ex, channelId));
        }
    }

    private static string Describe(HttpException exception, ulong channelId) => (int?)exception.DiscordCode switch
    {
        UnknownChannel => $"Discord channel {channelId} does not exist.",
        MissingAccess => $"The bot cannot see Discord channel {channelId}: give it the View Channel permission there.",
        MissingPermissions => $"The bot may not post in Discord channel {channelId}: give it the Send Messages and Embed Links permissions there.",
        CannotSendInNonTextChannel => $"Discord channel {channelId} is not a text channel.",
        _ => $"Discord rejected the message ({(int)exception.HttpCode}): {exception.Reason ?? exception.Message}",
    };
}
