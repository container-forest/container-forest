# Discord bot: gateway and menu

The bot's code conventions: [src/DiscordBot/CLAUDE.md](../../src/DiscordBot/CLAUDE.md); deployment: [installation.md](../installation.md#4-discord-bot-optional); generating the token: [development.md](../development.md#discord-bot).

## Gateway and token

- **Contract:** `src/Contracts/bot/bot.proto` (package `bot`); the bot is only a client. The service token goes in the `x-bot-token` metadata, and the user's Discord ID is a field of every request. Errors are gRPC status codes; for `UNAUTHENTICATED` the `x-cf-reason` trailer gives the reason (`invalid-bot-token` / `account-not-linked`).
- **The token is set in configuration, not issued by an endpoint:** it's as powerful as an administrator, so only someone with access to the aggregator's server should create it, not any administrator session in a browser.
  - Aggregator: `CF_BOTGATEWAY__TOKENHASHES__0` (`__1`, …) holds the token's SHA-256, 64 lowercase hex characters, as `SecretToken.Hash` produces; `SecretToken.Matches` verifies it. A malformed hash stops the start (`ValidateOnStart`). With no hash, `BotGateway` rejects every call (`invalid-bot-token`).
  - Several hashes allow rotation without downtime: add the new one → restart the aggregator → switch the bot → remove the old one.
  - Tokens in the database only once several bots or rotation from the panel are needed; the contract won't change.
- **`BotGatewayService`** (on the same gRPC port as agents): every call made on behalf of a user puts their `UserId` into `HttpContext.User`, so the same services and permission checks as for the panel apply.
- **`BotGatewayInterceptor`** maps exceptions to status codes and checks the token for every kind of call, streaming ones included: the base `Interceptor` lets those through unchecked.

## Account linking

A linked Discord account is the domain entity `DiscordLink` (table `DiscordLinks`, one record per user, Discord ID unique), not an ASP.NET Identity external login: the bot doesn't depend on Identity, and linking works before any sign-in. The main way is OAuth2 in the panel (ADR-0009); the alternative is a one-time code from the panel (`/api/me/discord/link-code`, only its hash is stored) and `/link <code>` in the bot (`LinkAccount`). Linking the same Discord account again moves the link to the new user.

## The /dashboard menu

How the route is encoded in `custom_id`, how deployments are followed and how big a page is are described in the comments of `DashboardRoute` and `DashboardService`. Code: `src/DiscordBot/Dashboard`, `Interactions/DashboardModule.cs`, `DashboardRenderer.cs`.

- **Message:** one ephemeral Components V2 message; every click edits it in place (defer → `ModifyOriginalResponseAsync` with `MessageFlags.ComponentsV2`).
- **Screens:** environments → stacks → stack → confirmation for redeploy and down; restart without confirmation, as in the panel.
- **An unknown id** (from an older menu version) opens the list of environments.
- **User:** every click runs on behalf of whoever clicked; the menu is ephemeral, so that's the same user.
- **Buttons** are hidden by `can_restart`, `can_take_down` and `can_deploy`, and disabled while the agent is offline or an action is running.
- **Tests:** the logic (`DashboardService`) doesn't depend on Discord.Net and is tested with an `IForestClient` stub; a test with `InteractionService.SearchComponentCommand` checks that the `[ComponentInteraction("dash:*")]` pattern catches every route.
- **Discord limits:** up to 40 components and 4000 characters of text per message, up to 5 buttons per row.
