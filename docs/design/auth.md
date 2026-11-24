# Authentication, users, tokens

Decisions: [ADR-0007](../adr/0007-api-tokens.md) (tokens), [ADR-0008](../adr/0008-cli.md) (CLI), [ADR-0009](../adr/0009-passwordless-sign-in.md) (passkeys, Discord). Backend paths are relative to `src/Aggregator`, panel paths to `src/Web`.

## The shared contract

- **Authentication and authorization are separated by one contract:** every scheme (cookie, API token, dev header, bot) puts the `UserId` into the `ClaimTypes.NameIdentifier` claim; `ClaimsCurrentUser` reads it as `ICurrentUser`, and `Application` services get the user only through `CurrentActor` (no user is a 401, a deactivated one a 403). The sign-in contract is `Api/Contracts/AccountContracts.cs`.
- **The default scheme** is the `Default` policy (`AuthenticationSetup`): Bearer → `ApiToken`, in `Development` the `X-Dev-User` header → `DevUser`, otherwise the cookie. Outside `Development` the header is ignored. `DevUser` names any user created through `/api/users` or the bootstrap, without a password; curl and the panel's `CF_DEV_USER` use it.

## Passwords and users

- **Password sign-in.** Passwords, lockout and security stamps are ASP.NET Identity's: `AccountUser : IdentityUser<Guid>` (`Infrastructure/Accounts`) has the same id as the domain `User`; `AggregatorDbContext` inherits `IdentityUserContext<AccountUser, Guid>` (tables `AspNetUsers*`, no role tables). There's no `AspNetUsers` → `Users` foreign key: EF won't join a `Guid` with a `UserId`; the account is created together with the domain user in one `SaveChanges` (`UserManager.CreateAsync` saves the context). Users once created through the removed `/api/dev/users` have no account: an administrator's password reset creates one.
- **User management** (`/api/users/*`, contract in `Api/Contracts/UserContracts.cs`): `UserManagementService`, administrators only. The service changes Identity accounts only through the `IAccountStore` port (`IdentityAccountStore`); every account change saves the context together with the domain changes. A password is checked against the policy before the hash changes, so a rejected password can't leave the account without one.
  - User changes form the `UserAccountAction` hierarchy (Command): check → act → end sessions (`EndsSessions`). `AdministratorRemovingAction` (deactivation, removing admin rights) won't remove the last active administrator (409). Deactivation, removing admin rights and a password reset end the user's sessions; renaming doesn't (Identity's `SetUserNameAsync` would change the security stamp, so the name changes through `UpdateAsync`).
  - Roles: one per scope (environment or stack); a second is a 409; a stack from another environment and someone else's assignment are a 404.
  - The policy is `AccountPolicy`: a minimum length, no composition rules, and lockout. The rest of NIST SP 800-63B is checked by `PasswordRule` rules (Identity's `IPasswordValidator`, `Infrastructure/Accounts/PasswordRules.cs`): length limits, no repeats or sequences, no user name or system name. A rule's error code starts with `Password`: that's how `IdentityAccountStore` tells a rejected password from other Identity errors. A rejected password is `PasswordRejectedException` → 400 with `errors` in problem details.
  - `PasswordSignIn`: an unknown name, a wrong password, lockout and a deactivated user all get the same 401; a hash is computed for an unknown name anyway (a decoy), so the response time doesn't reveal which names exist.
  - `/api/auth/login` and `/api/auth/password` are rate-limited per client address (`AuthEndpoints.SignInPolicy`, else 429): lockout protects one account, not one password tried against many names. Behind Caddy, the address comes from `X-Forwarded-For`.
  - The `cf_session` cookie: HttpOnly, `SameSite=Strict`, `Secure` `Always` (`SameAsRequest` in `Development`); `RememberMe` makes it persistent, otherwise it's a session cookie. The security stamp is checked on every request (`ValidationInterval = 0`): changing the password ends other sessions at once and reissues the current one (`RefreshSignInAsync`). The price is a database query and a new cookie on every response.
  - The first administrator: after migrations, if the database has no users, `AdminBootstrapper` creates one from `CF_BOOTSTRAP__ADMINUSERNAME` / `CF_BOOTSTRAP__ADMINPASSWORD` (set both or neither, `ValidateOnStart`). The password isn't logged; with neither the variables nor any users there's a warning in the log.

## Two-factor authentication

The flow, what it covers and the requirement for administrators are in ADR-0019.

- **Code:** `Infrastructure/Accounts/TotpAuthenticator.cs` (the token provider, `options.Tokens.AuthenticatorTokenProvider`), `ProtectedAccountStore.cs` (the key and the recovery codes encrypted, `SecretPurpose.AuthenticatorSecret`), `Api/Access/TwoFactorSignIn.cs` (the second step, the `cf_2fa` cookie, `SecretPurpose.TwoFactorSignIn`), `TwoFactorManagement.cs`, `TwoFactorEnrollment.cs` (the middleware after `ReadOnlyTokens`), `Endpoints/TwoFactorEndpoints.cs`, `Application/Access/SignInSettings.cs`.
- **Sign-in:** `PasswordSignIn` returns `PasswordSignInResult`; `/api/auth/login` answers 200 with `MeResponse` or 202 with `TwoFactorChallengeResponse`. Six digits are an app code, anything else is tried as a recovery code. Wrong codes, an expired step and a changed stamp all get the same 401.
- **Replay:** the last accepted step is the token `[ContainerForest]/LastTotpStep` in `AspNetUserTokens`; it is deleted when two-factor is turned off.
- **How a session signed in:** `SignInMethods` (`pwd`, `mfa`, `passkey`, `Discord`) in the `ClaimTypes.AuthenticationMethod` claim; `SecurityStampValidatorOptions.OnRefreshingPrincipal` copies it, since the validator rebuilds the principal on every request. `TwoFactorManagement.RequiresSetupAsync` decides by it.
- **Discord as a password:** with `SignIn:RequireTwoFactorAfterDiscord`, `DiscordSignIn` starts the `cf_2fa` step for an account with the app and sends the browser to `/login?step=code`.
- **State in `MeResponse`:** `twoFactorEnabled` (the confirmation dialog asks for the code), `twoFactorSetupRequired` (the panel's `_authed` layout sends the user to `/account`).
- **Stamps:** setting up, turning on and turning off change the security stamp; the user's other sessions end and the current one is signed in again (`RefreshSignInAsync`).
- **Tests:** `tests/Aggregator/Api/TwoFactorTests.cs` with a movable clock (one code per 30-second step), `tests/Aggregator/Infrastructure/Accounts/TotpAuthenticatorTests.cs` (the RFC 6238 vectors).

## API tokens

Format, restriction rules and reasons are in ADR-0007.

- **Code:** `Domain/Access/ApiToken.cs`, `Api/Access/ApiTokenAuthenticationHandler.cs`, `/api/me/tokens`, `/api/users/{id}/tokens`. The hint stored in the database is the first 6 characters after the prefix. The scheme skips a Bearer of another shape (the deploy hook) with `NoResult`.
- **Restrictions** live in claims (`ApiTokenClaims`: `cf:api-token`, `cf:read-only`, `cf:restricted`, `cf:environment`); `ClaimsCurrentUser` exposes them as `ICurrentUser.Scope` and `ApiTokenId`. `AccessPolicy` decides on an `Actor(User, AccessScope)`.
- **Read-only:** `ReadOnlyTokens` (middleware after authentication) answers 403 to any request other than GET/HEAD/OPTIONS, except endpoints with `AllowReadOnlyTokens()`.
- **Session only:** a new service method that changes the account itself or issues credentials gets the user through `CurrentActor.GetSignedInUserAsync`. The live hub uses the `Session` policy.

## Passkeys

The flow, the state cookie and the management rules are in ADR-0009.

- **Code:** `Api/Access/PasskeySignIn.cs`, `PasskeyManagement.cs`, `PasskeyContracts.cs`.
- **A migration pitfall.** The Identity schema is version 3 (`AccountPolicy`: `Stores.SchemaVersion`). `IdentityUserContext` builds its model from the application services' `IdentityOptions`, not from the `SchemaVersion` property: `DesignTimeDbContextFactory` passes `AccountPolicy.Configure` through `UseApplicationServiceProvider`, otherwise migrations don't see the passkey tables.
- **Sign-in:** `MakePasskeyRequestOptionsAsync(null)` → `PerformPasskeyAssertionAsync`, a check of lockout and the domain user being active, `AddOrUpdatePasskeyAsync` for the counter, `SignInAsync(isPersistent: rememberMe)`. The options are Identity's raw JSON (`TypedResults.Text`).
- **Management:** the id is the credential id in base64url; an attestation is accepted only if `UserEntity.Id` is the current user.
- **Tests:** `tests/Aggregator/Api/PasskeyTests.cs` with the software authenticator `FakeAuthenticator` (ES256, CBOR through `System.Formats.Cbor`, `none` attestation); the test's requests carry an `Origin` header.

## Discord OAuth2

The flow, choosing `DiscordLink` over `AspNetUserLogins`, and working around `SameSite=Strict` are in the addendum to ADR-0009.

- **Code:** `Api/Access/DiscordSignIn.cs`, `Endpoints/DiscordAuthEndpoints.cs`.
- **Registering the scheme:** only with `DiscordAuth:ClientId` and `ClientSecret` (`DiscordAuthOptions.IsConfigured`), otherwise the endpoints are a 404. `SignInScheme` is `IdentityConstants.ExternalScheme`.
- **Service:** sign-in is `DiscordLinkService.FindUserForSignInAsync`, linking `LinkVerifiedAsync`.
- **State:** the purpose (`cf.purpose`), the return URL and the linking user's id are in `AuthenticationProperties.Items`. A page instead of a 302 is `ContinuePage`; the return URL is checked in `SafePath`, and failures are handled in `OnRemoteFailure` too.
- **OpenAPI:** the endpoints are browser navigations, so they aren't described (`ExcludeFromDescription`).
- **Tests:** `DiscordSignInTests` with a fake Discord on `BackchannelHttpHandler` (through `AggregatorApp.Customize`).

## CLI sign-in

The flow, lifetimes and limits are in ADR-0008.

- **Code:** `CliAuthorizationService`, `Domain/Access/CliAuthorization.cs`, `CliAuthorizationEndpoints`. `OneTimeCode` generates the `userCode`, shown as `XXXX-XXXX`.
- **The token's restrictions** are checked as for `/api/me/tokens` (`ApiTokenService.EnsureRoomForTokenAsync`, `EnsureEnvironmentsVisibleAsync`).
- **Issuing:** `ExchangeAsync` creates the token (`CliAuthorization.IssueToken`), and the request is deleted afterwards; a user deactivated after approving gets `Denied`.
- **Rate limits:** behind Caddy, the client's address comes from `X-Forwarded-For`.
- **Panel:** `/cli/authorize?code=&port=` (after the decision, `window.location.assign("http://127.0.0.1:<port>/done?result=…")`) and `/cli/device`.

## Fresh identity confirmation

Sensitive actions (changing system settings) need not just a session but a freshly entered password or passkey: [settings and confirmation](backup.md#identity-confirmation).
