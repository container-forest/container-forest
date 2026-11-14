# ADR-0009. Passwordless sign-in: passkeys and Discord

- Status: accepted
- Date: 2026-10-09

## Context

The panel had only password sign-in. Passwords are reused and phished, and typing a long passphrase on a phone is awkward, yet a phone is exactly where people restart a crashed service. WebAuthn (passkeys) binds sign-in to the panel's domain and to a key on the person's device: a phishing site can't use it, and the server holds only a public key.

Options considered:

1. **A WebAuthn library** (Fido2NetLib on the server, `@simplewebauthn/browser` in the panel). Mature, but that's two dependencies and tables of our own for the keys.
2. **ASP.NET Core Identity passkeys in .NET 10** (chosen). Identity already handles accounts (password sign-in, lockout, security stamps), and in .NET 10 it does WebAuthn itself: options, attestation and assertion verification, the `AspNetUserPasskeys` table. The panel uses the browser's built-in `PublicKeyCredential.parse*OptionsFromJSON()` and `toJSON()`, with no library.
3. **A passkey as a second factor after the password.** Protects against a stolen password but doesn't get rid of it; passwordless sign-in was chosen, with 2FA separately later.

## Decision

1. **Identity schema version 3** (`IdentityOptions.Stores.SchemaVersion`, `AccountPolicy`) adds the `AspNetUserPasskeys` table (migration `Passkeys`). `IdentityUserContext` builds its model from the application services' `IdentityOptions`, so the context factory for migrations (`DesignTimeDbContextFactory`) passes the same settings; without that the migration comes out empty.
2. **Sign-in** (`PasskeySignIn`): `POST /api/auth/passkey/options` → the browser → `POST /api/auth/passkey/login`. The options are for any user (discoverable credentials): the browser offers this site's keys itself, with no name to type. The session is the same `cf_session` as after a password. An unknown key, a failed check, lockout and a deactivated user all get the same 401. The signature counter is updated after every sign-in, so a cloned authenticator can be noticed.
3. **The state between the options and the response** is kept by Identity in the `TwoFactorUserId` scheme's cookie; for us that's `cf_passkey`: HttpOnly, SameSite=Strict, Secure outside Development, 5 minutes. Both requests must come from the same browser.
4. **Management** (`PasskeyManagement`, `/api/me/passkeys`) is sessions only, not API tokens (like the password and tokens, ADR-0007): a token mustn't add a way to sign in. Up to 20 keys per user, a name of up to 64 characters. A key is added only if the options were created for the same user. An administrator sees and removes anyone's keys (`/api/users/{id}/passkeys`): a lost device.
5. **The domain (RP ID)** is the request's host; behind Caddy, the panel's domain (ADR-0005). The origin is checked as Identity does by default: the request's `Origin` header must match the origin in the browser's response.
6. **Panel:** a *Sign in with a passkey* button and keys in the user name field's autofill (`autocomplete="username webauthn"`, conditional mediation), a *Passkeys* section under *Account*, and a user's keys card for administrators. A browser without `parse*OptionsFromJSON` (before Chrome 129, Firefox 119, Safari 18.4) doesn't see the button. `GET /api/auth/methods` tells the sign-in screen which methods are enabled.

## Consequences

- Passwordless sign-in works with any WebAuthn authenticator: platform ones (Windows Hello, Touch ID, Android), password managers, hardware keys.
- ~~Anyone with an active session can add a passkey~~: since ADR-0013, adding one needs a fresh password or passkey, except for the first key of a Discord-only account. Other protection: a short session lifetime without *Remember me*, ending sessions when the password changes, and an administrator removing keys.
- Attestation isn't verified (`none`): we don't need to know the authenticator's model.
- Discord sign-in: the section below.

## Addendum: sign-in and linking through Discord OAuth2

### Context

Linking a Discord account (for the bot) was only a one-time code from the panel that the person enters in Discord with `/link`. Most users already have a Discord account; signing in to the panel with it and linking it with one click is natural for a tool where Discord is a full interface.

Options considered:

1. **An ASP.NET Identity external login** (`AspNetUserLogins`). The standard path, but then the Discord account would be stored twice, for Identity sign-in and for the bot in `DiscordLink`, and the two could drift apart.
2. **`DiscordLink` as the single identity** (chosen): the same record as for the bot. Linking by code and through OAuth2 write the same thing; Discord sign-in looks the user up by `DiscordLink`.
3. **Our own OAuth2 implementation.** Rejected: a protocol with PKCE, state and correlation cookies is better taken ready-made.

### Decision

1. **The `AspNet.Security.OAuth.Discord` package** (aspnet-contrib): a Discord OAuth2 handler on top of ASP.NET Core's built-in `OAuthHandler`: PKCE, a `state` protected by Data Protection, a correlation cookie. The scope is only `identify`. The Discord application is the same as the bot's; `CF_DISCORDAUTH__CLIENTID` and `CF_DISCORDAUTH__CLIENTSECRET` (the secret is like the bot's token, only in the aggregator's `.env`). Without them Discord sign-in is off, and `GET /api/auth/methods` tells the panel so.
2. **Sign-in:** `GET /api/auth/discord/login?redirect=/path` → Discord → `/api/auth/discord/callback` (the handler, external cookie `cf_external`) → `/api/auth/discord/complete`: the user this Discord account is linked to gets a `cf_session`. **There's no sign-up through Discord:** an unlinked account, a deactivated user and lockout all lead to the same `/login?error=discord`. The name in the link is updated on every sign-in.
3. **Linking:** `GET /api/me/discord/oauth` (from the panel, sessions only; without one, to `/login`) → Discord → `/api/auth/discord/complete` → `/discord?linked=discord`. The id of the user who started linking travels in the `state`, protected by Data Protection: on the way back there's no session cookie (below). A Discord account linked to another user is moved, as with a code. The code remains an alternative.
4. **SameSite=Strict and a foreign site.** Discord sends the browser back from another site, so a cookie set in that redirect chain (`cf_session`) isn't sent on the next step. That's why `/api/auth/discord/complete` answers not with a redirect but with a page with `<meta http-equiv="refresh">`: the next navigation starts on our site. The correlation cookie and `cf_external` are `SameSite=Lax`, since they must survive the return from Discord; they live for minutes.
5. **Sign-in CSRF.** The correlation cookie binds the flow to the browser that started it: a callback slipped into someone else's browser (to link the victim's Discord to the attacker's account) is rejected. The return URL is only a path on this site (`/x`, not `//host`).

### Consequences

- A compromised Discord account now gives sign-in to the panel, not only actions through the bot; the rights are the same. Turning on 2FA in Discord is recommended.
- A new dependency; the Discord application must register the redirect `https://<domain>/api/auth/discord/callback`.
