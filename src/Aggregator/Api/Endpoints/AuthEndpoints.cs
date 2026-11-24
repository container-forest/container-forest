using System.Threading.RateLimiting;
using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Password sign-in for the panel: the <c>cf_session</c> cookie (see AccountContracts).</summary>
internal static class AuthEndpoints
{
    /// <summary>Password sign-in and the current password of a password change: both check a password someone may guess.</summary>
    public const string SignInPolicy = "sign-in";

    /// <summary>
    /// Per client address: lockout stops guessing one user's password, but not trying a common password on many
    /// names. A person who mistypes a few times stays far below the limit.
    /// </summary>
    /// <remarks>Rejections are 429: <see cref="CliAuthorizationEndpoints.AddCliAuthorizationRateLimits"/> sets it for all policies.</remarks>
    public static RateLimiterOptions AddSignInRateLimit(this RateLimiterOptions options)
    {
        options.AddPolicy(SignInPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromSeconds(6),
            }));
        return options;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Account");

        group.MapPost("/login", async Task<Results<Ok<MeResponse>, Accepted<TwoFactorChallengeResponse>, ProblemHttpResult>> (
                LoginRequest request, PasswordSignIn signIn, MeService me, CancellationToken ct) =>
            {
                var result = await signIn.SignInAsync(request.UserName, request.Password, request.RememberMe, ct);
                return result switch
                {
                    { User: { } user } => TypedResults.Ok(await me.DescribeAsync(user, ct, SignInMethods.Password)),
                    { TwoFactorRequired: true } => TypedResults.Accepted((string?)null, new TwoFactorChallengeResponse(TwoFactorSignIn.Lifetime.TotalSeconds)),
                    _ => TypedResults.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Unauthorized",
                        detail: "Wrong user name or password."),
                };
            })
            .WithName("Login")
            .RequireRateLimiting(SignInPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/login/two-factor", async Task<Results<Ok<MeResponse>, ProblemHttpResult>> (
                TwoFactorLoginRequest request, TwoFactorSignIn signIn, MeService me, CancellationToken ct) =>
            {
                var user = await signIn.CompleteAsync(request.Code, ct);
                return user is null
                    ? TypedResults.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Unauthorized",
                        detail: "Wrong code, or the sign-in expired: enter the password again.")
                    : TypedResults.Ok(await me.DescribeAsync(user, ct, SignInMethods.TwoFactor));
            })
            .WithName("LoginTwoFactor")
            .RequireRateLimiting(SignInPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", async (PasswordSignIn signIn, CancellationToken ct) =>
            {
                await signIn.SignOutAsync(ct);
                return TypedResults.NoContent();
            })
            .WithName("Logout");

        group.MapPost("/password", async (ChangePasswordRequest request, PasswordSignIn signIn, CancellationToken ct) =>
            {
                await signIn.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, ct);
                return TypedResults.NoContent();
            })
            .WithName("ChangePassword")
            .RequireRateLimiting(SignInPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }
}
