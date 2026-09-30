using ContainerForest.DiscordBot.Commands;
using Discord;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary><see cref="ICommandInteraction"/> over a Discord.Net slash command interaction.</summary>
internal sealed class DiscordInteraction(IDiscordInteraction interaction) : ICommandInteraction
{
    public BotUser User { get; } = new(interaction.User.Id, interaction.User.Username);

    // Ephemeral from the start: a deferred reply keeps the visibility it was deferred with.
    public Task DeferAsync(CancellationToken cancellationToken) =>
        interaction.DeferAsync(ephemeral: true, Options(cancellationToken));

    public Task RespondAsync(BotReply reply, CancellationToken cancellationToken) =>
        interaction.ModifyOriginalResponseAsync(
            message =>
            {
                message.Content = reply.Content;
                // Text from the aggregator or Docker must not ping anyone, e.g. a container named "@everyone".
                message.AllowedMentions = AllowedMentions.None;
            },
            Options(cancellationToken));

    private static RequestOptions Options(CancellationToken cancellationToken) => new() { CancelToken = cancellationToken };
}
