namespace ContainerForest.DiscordBot.Configuration;

/// <summary>Configuration section "Bot" (env: CF_BOT__...): how the bot reaches the aggregator.</summary>
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>The aggregator's gRPC endpoint, e.g. https://forest.example.com or http://localhost:5081.</summary>
    public Uri? ControllerUrl { get; set; }

    /// <summary>
    /// The bot's service token, sent in "x-bot-token". The aggregator keeps its SHA-256 hash in
    /// BotGateway:TokenHashes. As powerful as an administrator's password: never log it.
    /// </summary>
    public string? ServiceToken { get; set; }

    /// <summary>Deadline for calls that only read or link, not for container actions.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Deadline for start, stop and restart. The aggregator allows a stop timeout of up to 5 minutes,
    /// and a Discord interaction token stays valid for 15 minutes.
    /// </summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromMinutes(6);

    internal bool IsValid() =>
        ControllerUrl is { IsAbsoluteUri: true } url
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(ServiceToken)
        && QueryTimeout > TimeSpan.Zero
        && ActionTimeout > TimeSpan.Zero;
}
