# ADR-0019. Two-factor authentication with an authenticator app

- Status: accepted
- Date: 2026-11-24

## Context

A password alone signs into the panel, and the panel controls every host. Passkeys (ADR-0009) are already two factors (a device and its PIN or biometrics), but not everyone has a device that supports them, and the password stays a way in. ADR-0009 left a second factor after the password for later.

Options considered:

1. **TOTP with an authenticator app** (chosen). Every phone has one; ASP.NET Core Identity already has the parts: `TwoFactorEnabled`, the authenticator key, recovery codes, lockout.
2. **A passkey as the second factor after the password.** Whoever has a passkey can sign in with it alone, so this adds little.
3. **Codes by email or SMS.** The aggregator sends no email, and SMS is the weakest second factor.

## Decision

1. **Only a password sign-in asks for the code.** A passkey is two factors already. Discord sign-in relies on Discord's own two-factor (ADR-0009 recommends turning it on). API tokens, the bot's token and the CLI's device flow (approved in a signed-in panel) are not changed.
2. **Two steps.** A right password of an account with two-factor answers 202 instead of a session, and puts its result in the `cf_2fa` cookie: the user, the security stamp and *Remember me*, protected with Data Protection, 5 minutes, HttpOnly, SameSite=Strict, path `/api/auth`. `POST /api/auth/login/two-factor` takes six digits of the app or a recovery code. Identity's own two-factor cookie is not used: it also carries the challenge of a passkey sign-in, which the sign-in screen asks for as soon as it opens. A changed stamp (a new password, two-factor turned off) voids the pending step.
3. **Lockout counts both.** A right password does not reset the failure count of such an account (Identity's behaviour); a wrong code adds to it. Both steps are rate-limited per address like sign-in.
4. **Codes are used once.** Our own token provider (`TotpAuthenticator`: RFC 6238, HMAC-SHA1, 30 s, 6 digits, one step of drift) remembers the last accepted step per user and refuses that step and earlier ones. Identity's provider accepts the same code for its whole window.
5. **Secrets at rest.** Identity keeps the authenticator key and the recovery codes in `AspNetUserTokens` in plain text; `ProtectedAccountStore` encrypts both with Data Protection (ADR-0003). There are 10 recovery codes, shown once.
6. **Managing it** (`/api/me/two-factor/*`) is sessions only, like passkeys. Setting up, turning off and new recovery codes need a fresh identity confirmation (ADR-0013); with two-factor on, confirming with a password also needs the code. Turning it on needs a code of the new key. An administrator turns another user's two-factor off for a lost phone (`DELETE /api/users/{id}/two-factor`, the `AccountSecurityChanged` notification); the CLI has `forest user reset-two-factor`.
7. **Required for administrators** (the system setting `SignIn:RequireTwoFactorForAdministrators`, ADR-0013, off by default; changing it needs a confirmation and sends `AccountSecurityChanged`). An administrator with neither the app nor a passkey gets a session that reaches only `/api/me`, `/api/auth/*`, `/api/me/two-factor/*` and `/api/me/passkeys/*`: everything else is 403 with code `TwoFactorSetupRequired`, and the panel opens *Account*. The rule is checked on every request, not at sign-in, so it also applies to sessions open when it is turned on. Sessions only: API tokens and the dev header are not limited.
8. **The QR code** is drawn in the browser from the `otpauth://` link with `uqr` (MIT, no dependencies) as SVG rectangles: the key never leaves the page, and no generated markup is inserted.

## Consequences

- A stolen password no longer signs in to an account with two-factor; a stolen session still acts as the user, as before.
- An administrator with a passkey and no app is not limited by the requirement, and can still sign in with the password alone. The passkey is the stronger factor; a stricter rule (the app required whenever a password is used) can come later.
- A user who loses both the phone and the recovery codes needs an administrator; an administrator who loses both needs another administrator, or the database.
- Identity's tables don't change: no migration.
