namespace ContainerForest.Aggregator.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/auth/login</c>. On success the aggregator sets the <c>cf_session</c> cookie
/// (HttpOnly, Secure, SameSite=Strict) and answers 200 with <see cref="MeResponse"/>. Wrong credentials give
/// 401 without saying which part was wrong. <c>POST /api/auth/logout</c> takes no body and answers 204.
/// </summary>
/// <remarks>The login puts the user's id into the NameIdentifier claim of the session (see ClaimsCurrentUser).</remarks>
public sealed record LoginRequest(string UserName, string Password, bool RememberMe);

/// <summary>The signed-in user: <c>GET /api/me</c>, and the answer to a successful login.</summary>
public sealed record MeResponse(Guid Id, string UserName, bool IsAdmin);
