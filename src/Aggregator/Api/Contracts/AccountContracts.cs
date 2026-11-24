namespace ContainerForest.Aggregator.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/auth/login</c>. On success the aggregator sets the <c>cf_session</c> cookie
/// (HttpOnly, Secure, SameSite=Strict) and answers 200 with <see cref="MeResponse"/>. Wrong credentials give
/// 401 without saying which part was wrong. <c>POST /api/auth/logout</c> takes no body and answers 204.
/// </summary>
/// <remarks>The login puts the user's id into the NameIdentifier claim of the session (see ClaimsCurrentUser).</remarks>
public sealed record LoginRequest(string UserName, string Password, bool RememberMe);

/// <summary>The signed-in user: <c>GET /api/me</c>, and the answer to a successful login.</summary>
/// <param name="TwoFactorEnabled">A password sign-in asks for a code of the authenticator app (ADR-0019).</param>
/// <param name="TwoFactorSetupRequired">
/// The system requires two-factor of this administrator and they have neither an app nor a passkey: the session may
/// only set one up (other requests are 403 with code <c>TwoFactorSetupRequired</c>).
/// </param>
public sealed record MeResponse(Guid Id, string UserName, bool IsAdmin, bool TwoFactorEnabled = false, bool TwoFactorSetupRequired = false);

/// <summary>
/// The answer (202) to <c>POST /api/auth/login</c> with the right password of an account with two-factor
/// authentication: send the code to <c>POST /api/auth/login/two-factor</c> within <paramref name="ExpiresInSeconds"/>.
/// The state between the steps is the <c>cf_2fa</c> cookie.
/// </summary>
public sealed record TwoFactorChallengeResponse(double ExpiresInSeconds);

/// <summary>Body of <c>POST /api/auth/login/two-factor</c>: six digits of the app, or one of the recovery codes.</summary>
public sealed record TwoFactorLoginRequest(string Code);

/// <summary><c>GET /api/me/two-factor</c>.</summary>
/// <param name="Required">The system requires it of this user, so it cannot be turned off.</param>
public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft, bool Required);

/// <summary>Body of the two-factor requests that need a fresh identity confirmation (the token from /api/auth/confirm/*).</summary>
public sealed record TwoFactorConfirmationRequest(string? Confirmation);

/// <summary>
/// The answer to <c>POST /api/me/two-factor/setup</c>: the new key for the app, shown only now. It is not in use until
/// a code from the app turns two-factor on.
/// </summary>
/// <param name="SharedKey">Base32, for typing into the app.</param>
/// <param name="Uri">The otpauth:// link for the QR code.</param>
public sealed record TwoFactorSetupResponse(string SharedKey, string Uri)
{
    public override string ToString() => nameof(TwoFactorSetupResponse);
}

/// <summary>Body of <c>POST /api/me/two-factor/enable</c>: the current code of the app just set up.</summary>
public sealed record EnableTwoFactorRequest(string Code);

/// <summary>Recovery codes, shown only in this answer: each works once instead of the app's code.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes)
{
    public override string ToString() => nameof(RecoveryCodesResponse);
}

/// <summary>Body of <c>PUT /api/settings/sign-in</c>; the change needs a fresh identity confirmation.</summary>
public sealed record SignInSettingsRequest(bool RequireTwoFactorForAdministrators, string? Confirmation);

/// <param name="Locked">Set by <c>CF_SIGNIN__REQUIRETWOFACTORFORADMINISTRATORS</c>; the panel cannot change it.</param>
public sealed record SignInSettingsResponse(bool RequireTwoFactorForAdministrators, bool Locked);

/// <summary>
/// Body of <c>POST /api/auth/password</c>: the signed-in user changes their own password and answers 204.
/// A wrong current password is 400; a new password that fails the policy (see UserContracts) is 400 with the reasons.
/// The user's other sessions end; the current one stays signed in.
/// </summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
