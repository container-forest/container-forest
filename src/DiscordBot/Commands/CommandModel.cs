namespace ContainerForest.DiscordBot.Commands;

/// <summary>The Discord user a command runs for.</summary>
public sealed record BotUser(ulong Id, string UserName);

/// <summary>
/// The text a command answers with. Every reply is ephemeral, visible only to the user who ran the command:
/// what a user may see (environments, container names) is theirs, and a channel may hold people with less access.
/// </summary>
public sealed record BotReply(string Content, bool IsError)
{
    public static BotReply Success(string content) => new(content, IsError: false);

    /// <summary>A failure the user can read; the message is escaped, it may come from the aggregator.</summary>
    public static BotReply Error(string message) => new("❌ " + Markdown.Escape(message), IsError: true);
}

/// <summary>A slash command interaction as the commands see it, without the Discord library.</summary>
public interface ICommandInteraction
{
    BotUser User { get; }

    /// <summary>Acknowledges the interaction; Discord requires it within 3 seconds.</summary>
    Task DeferAsync(CancellationToken cancellationToken);

    Task RespondAsync(BotReply reply, CancellationToken cancellationToken);
}

/// <summary>Arguments of a command that has none.</summary>
public readonly record struct NoArgs;
