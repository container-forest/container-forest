using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Fresh proof of identity for sensitive actions (see BackupContracts).</summary>
internal static class IdentityConfirmationEndpoints
{
    public static IEndpointRouteBuilder MapIdentityConfirmationEndpoints(this IEndpointRouteBuilder app)
    {
        var confirm = app.MapGroup("/api/auth/confirm")
            .WithTags("Account")
            .RequireRateLimiting(AuthEndpoints.SignInPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        confirm.MapPost("/password", async Task<Results<Ok<IdentityConfirmationResponse>, ProblemHttpResult>> (
                ConfirmWithPasswordRequest request, IdentityConfirmer confirmer, CancellationToken ct) =>
                Respond(await confirmer.WithPasswordAsync(request.Password, request.Code, ct), "The password or the code was not accepted."))
            .WithName("ConfirmIdentityWithPassword");

        confirm.MapPost("/passkey/options", async (IdentityConfirmer confirmer, CancellationToken ct) =>
                await confirmer.MakePasskeyOptionsAsync(ct) is { } options
                    ? TypedResults.Text(options, "application/json")
                    : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Confirmation failed", detail: "This user has no passkeys."))
            .WithName("GetConfirmationPasskeyOptions")
            .Produces(StatusCodes.Status200OK, contentType: "application/json");

        confirm.MapPost("/passkey", async Task<Results<Ok<IdentityConfirmationResponse>, ProblemHttpResult>> (
                ConfirmWithPasskeyRequest request, IdentityConfirmer confirmer, CancellationToken ct) =>
                Respond(await confirmer.WithPasskeyAsync(request.Credential, ct), "The passkey was not accepted."))
            .WithName("ConfirmIdentityWithPasskey");

        return app;
    }

    // 403, not 401: the session is fine, and a 401 would send the panel to the sign-in screen.
    private static Results<Ok<IdentityConfirmationResponse>, ProblemHttpResult> Respond(IssuedConfirmation? confirmation, string failure) =>
        confirmation is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Confirmation failed", detail: failure)
            : TypedResults.Ok(new IdentityConfirmationResponse(confirmation.Token, confirmation.ExpiresAt));
}
