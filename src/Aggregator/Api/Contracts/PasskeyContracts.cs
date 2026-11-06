namespace ContainerForest.Aggregator.Api.Contracts;

// Passkeys (WebAuthn) of ASP.NET Core Identity, ADR-0009.
//
//   GET    /api/auth/methods                -> SignInMethodsResponse; anonymous: which buttons /login shows
//   POST   /api/auth/passkey/options        -> options for navigator.credentials.get(), as JSON; anonymous
//   POST   /api/auth/passkey/login          -> MeResponse and the cf_session cookie; 401 for any failure
//   POST   /api/me/passkeys/options         -> options for navigator.credentials.create(), as JSON; a session only
//   GET    /api/me/passkeys                 -> PasskeyResponse[], oldest first; a session only
//   GET    /api/me/passkeys/needs-confirmation -> PasskeyConfirmationResponse; a session only
//   POST   /api/me/passkeys                 -> 201 PasskeyResponse; a session only and, when the account has a password or
//                                              a passkey, a fresh confirmation (403 without one); 400 when the credential does not check out
//   PUT    /api/me/passkeys/{id}            -> PasskeyResponse; renames; a session only
//   DELETE /api/me/passkeys/{id}            -> 204; a session only
//   GET    /api/users/{id}/passkeys         -> PasskeyResponse[]; administrators
//   DELETE /api/users/{id}/passkeys/{key}   -> 204; administrators, e.g. for a lost device
//
// The options carry a challenge that the aggregator keeps in a short-lived cookie (cf_passkey) until the browser
// answers with the credential, so the two calls must come from the same browser. Passkeys are never managed with
// an API token: a token must not be able to add a way to sign in.

/// <param name="Credential">The result of <c>navigator.credentials.get()</c>, serialized with <c>toJSON()</c>.</param>
public sealed record PasskeyLoginRequest(string Credential, bool RememberMe);

/// <param name="Credential">The result of <c>navigator.credentials.create()</c>, serialized with <c>toJSON()</c>.</param>
/// <param name="Name">How the user tells the passkey apart, e.g. "Laptop" or "YubiKey"; 1-64 characters.</param>
/// <param name="Confirmation">The token from /api/auth/confirm/*; not needed for the first credential of an account.</param>
public sealed record AddPasskeyRequest(string Credential, string Name, string? Confirmation);

/// <param name="Required">Whether adding a passkey needs a fresh confirmation; false only for an account with nothing to confirm with.</param>
public sealed record PasskeyConfirmationResponse(bool Required);

public sealed record RenamePasskeyRequest(string Name);

/// <param name="Id">The credential id in base64url.</param>
/// <param name="IsBackedUp">Synced to other devices by the provider (e.g. a password manager), not bound to one device.</param>
/// <param name="Transports">How the browser reaches the authenticator: internal, usb, nfc, ble, hybrid.</param>
public sealed record PasskeyResponse(string Id, string Name, DateTimeOffset CreatedAt, bool IsBackedUp, IReadOnlyList<string> Transports);

/// <summary>Ways to sign in this aggregator offers.</summary>
public sealed record SignInMethodsResponse(bool Password, bool Passkey, bool Discord);
