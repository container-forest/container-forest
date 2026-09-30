namespace ContainerForest.DiscordBot.Configuration;

/// <summary>Configuration section "Discord" (env: CF_DISCORD__...): the bot's own Discord application.</summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>The Discord bot token from the Developer Portal. Never log it; locally it lives in user secrets.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// The test server for development. Slash commands registered on a server appear at once; global ones can
    /// take up to an hour, so Development registers here and other environments globally.
    /// </summary>
    public ulong? DevGuildId { get; set; }

    // A token goes into an HTTP header, which takes ASCII only. A stray character from another keyboard layout
    // would otherwise surface as endless reconnects with "Request headers must contain only ASCII characters".
    internal bool IsValid(bool isDevelopment) =>
        !string.IsNullOrWhiteSpace(Token)
        && Token.All(char.IsAscii)
        && !Token.Any(char.IsWhiteSpace)
        && (!isDevelopment || DevGuildId is > 0);
}
