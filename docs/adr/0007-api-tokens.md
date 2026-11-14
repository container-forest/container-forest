# ADR-0007. Personal API tokens and the actor's scope

- Status: accepted
- Date: 2026-10-09

## Context

The panel and the bot authenticate a person: a session cookie, or a Discord account linked to a user. The CLI, scripts and CI need credentials that live in a file or an environment variable, not in a browser. Such credentials leak more often (shell history, CI logs, someone else's laptop), so their rights should be narrower than the person's, not equal to them.

Options considered:

1. **A token = the user.** The simplest: a token has exactly its user's rights. An administrator's token for one stack's CI then controls the whole system.
2. **Separate service accounts with their own roles.** Flexible, but it duplicates the user model: roles, deactivation, who the owner is. Too much for a small team.
3. **A token with its own restrictions on top of the user's roles** (chosen): read-only, a list of environments, an expiry. Restrictions only take rights away, never add them.

A separate question is where to check the restrictions. An HTTP-level filter (the environment from the route) doesn't cover lists (`GET /api/environments`, repositories, connections) or public environments visible to everyone. Permissions are checked in one place (`AccessPolicy`, invariant 4), so token restrictions belong there too.

## Decision

1. **A token** (`ApiToken`, tables `ApiTokens`, `ApiTokenEnvironments`) belongs to a user: a name, `IsReadOnly`, `IsRestricted` with a list of environments, `ExpiresAt` (or no expiry, a deliberate choice), `LastUsedAt` (at most once a minute). The format is `cf_pat_` + 32 random bytes in base64url; secret scanners recognise the prefix. Only the SHA-256 is stored (invariant 8), plus the first characters as a hint in the list. The limit is 50 tokens per user.

2. **An actor instead of a user.** `CurrentActor.GetAsync` returns an `Actor(User, AccessScope)`; `AccessPolicy` takes an `Actor`. `AccessScope.Full` is a panel session or the bot; the rest are token restrictions. The rules:
   - an environment not on the list doesn't exist for the token, even a public one (`CanSee` is false, there's no role);
   - a restricted token has no administrator rights (`Actor.IsAdmin` is false, `GetAdministratorAsync` refuses); an administrator's token is an Editor in its environments;
   - read-only allows only `Permission.View` and `ViewLogs`;
   - a deactivated user gets a 403 with any token, as with a session.

   An environment deleted later disappears from the list through the cascade. That's why being restricted is a separate flag: a token with no environments left sees nothing, not everything.

3. **Read-only at the HTTP level too.** Administrative services check `GetAdministratorAsync`, not a `Permission`, so `ReadOnlyTokens` rejects any non-GET request with a read-only token (403) before the endpoint. The exception is `DELETE /api/me/tokens/current` (`AllowReadOnlyTokens`): a token may revoke itself.

4. **A token can't widen itself.** Only a session manages tokens, the password and Discord linking (`CurrentActor.GetSignedInUserAsync`): otherwise a restricted token could create an unrestricted one, or link a Discord account through which the bot acts with all the user's rights. The panel's live hub is for sessions only (the `Session` policy).

5. **Authentication.** The `ApiToken` scheme reads `Authorization: Bearer cf_pat_…` and puts the restrictions into claims, where `ClaimsCurrentUser` picks them up. The default scheme is a policy scheme: a Bearer means a token, otherwise (in Development) `X-Dev-User`, otherwise the cookie. A Bearer of another shape (the deploy hook's token) is skipped without an error: its own endpoint checks it. An unknown, revoked or expired token gets the same 401. Neither the token nor its hash is logged.

6. **Who manages them.** A user creates, sees and revokes their own tokens on the *API tokens* screen (`/api/me/tokens`); a token is shown once. An administrator sees and revokes anyone's tokens (`/api/users/{id}/tokens`) but doesn't create them in someone else's name.

## Consequences

- Every permission check gets an actor rather than a user; code that takes the user from `CurrentActor` automatically respects token restrictions. The implicit `User → Actor` conversion gives a full scope: that's how checks work for the bot and in tests.
- The CLI (the next step) authenticates with a token; getting one through the browser is a separate authorization flow that also creates the token on behalf of a session.
- Rotation is a new token and revoking the old one; there's no automatic rotation.
