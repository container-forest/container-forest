using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

/// <summary>
/// A slash command. <see cref="ExecuteAsync"/> is the same for all of them: acknowledge the interaction, call the
/// aggregator, format the answer, turn failures into a readable reply. Subclasses supply the call and the format.
/// </summary>
/// <typeparam name="TArgs">The command's options.</typeparam>
/// <typeparam name="TResult">What the aggregator returned, passed from the call to the format.</typeparam>
public abstract class BotCommand<TArgs, TResult>(ILogger logger)
{
    public const string UnexpectedErrorMessage = "Something went wrong. Try again later.";

    /// <summary>The slash command's name, for the log.</summary>
    public abstract string Name { get; }

    public async Task<BotReply> ExecuteAsync(ICommandInteraction interaction, TArgs args, CancellationToken cancellationToken)
    {
        // Calls to the aggregator can take minutes (a stop waits for the container), far over Discord's 3 seconds.
        await interaction.DeferAsync(cancellationToken);

        BotReply reply;
        try
        {
            var result = await CallForestAsync(interaction.User, args, cancellationToken);
            reply = Format(result, args);
        }
        catch (ForestException ex)
        {
            reply = BotReply.Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "/{Command} failed for Discord user {DiscordUserId}", Name, interaction.User.Id);
            reply = BotReply.Error(UnexpectedErrorMessage);
        }

        await interaction.RespondAsync(reply, cancellationToken);
        return reply;
    }

    protected abstract Task<TResult> CallForestAsync(BotUser user, TArgs args, CancellationToken cancellationToken);

    protected abstract BotReply Format(TResult result, TArgs args);
}
