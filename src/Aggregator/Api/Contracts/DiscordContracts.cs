namespace ContainerForest.Aggregator.Api.Contracts;

// Linking the signed-in user's Discord account, so the bot can act for them (src/Contracts/bot/bot.proto).
//
//   GET    /api/me/discord             -> DiscordLinkResponse
//   POST   /api/me/discord/link-code   -> 201 DiscordLinkCodeResponse
//   DELETE /api/me/discord             -> 204; the bot can no longer act for the user
//
// The user types "/link <code>" in Discord; the bot passes the code to LinkAccount. A new code replaces the
// previous one. In the final version the Discord OAuth2 login links the account too; both ways store the
// same link (the DiscordLink entity).

/// <param name="DiscordUserName">Null when not linked.</param>
public sealed record DiscordLinkResponse(bool Linked, string? DiscordUserName, DateTimeOffset? LinkedAt);

/// <param name="Code">Single use; short enough to type, e.g. 8 characters without look-alikes (0/O, 1/l).</param>
public sealed record DiscordLinkCodeResponse(string Code, DateTimeOffset ExpiresAt);
