using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

// Two-factor authentication with an authenticator app (ADR-0019), sessions only:
//
//   GET    /api/me/two-factor                -> TwoFactorStatusResponse
//   POST   /api/me/two-factor/setup          -> TwoFactorSetupResponse; needs a confirmation; 409 when it is already on
//   POST   /api/me/two-factor/enable         -> RecoveryCodesResponse; 400 for a wrong code
//   POST   /api/me/two-factor/recovery-codes -> RecoveryCodesResponse; needs a confirmation; the old codes stop working
//   POST   /api/me/two-factor/disable        -> 204; needs a confirmation; 409 when the system requires it of this user
//   DELETE /api/users/{id}/two-factor        -> 204; administrators; for a lost phone, the user's sessions end
//   GET    /api/settings/sign-in             -> SignInSettingsResponse; administrators
//   PUT    /api/settings/sign-in             -> SignInSettingsResponse; administrators, with a confirmation
//
// A missing or invalid confirmation is 403 (IdentityConfirmationRequiredException).
internal static class TwoFactorEndpoints
{
    public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/me/two-factor")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mine.MapGet("/", async (TwoFactorManagement twoFactor, CancellationToken ct) =>
                ToResponse(await twoFactor.GetMineAsync(ct)))
            .WithName("GetTwoFactor");

        mine.MapPost("/setup", async (TwoFactorConfirmationRequest request, TwoFactorManagement twoFactor, CancellationToken ct) =>
            {
                var setup = await twoFactor.BeginSetupAsync(request.Confirmation, ct);
                return new TwoFactorSetupResponse(setup.SharedKey, setup.Uri);
            })
            .WithName("BeginTwoFactorSetup")
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapPost("/enable", async (EnableTwoFactorRequest request, TwoFactorManagement twoFactor, CancellationToken ct) =>
                new RecoveryCodesResponse(await twoFactor.EnableAsync(request.Code, ct)))
            .WithName("EnableTwoFactor")
            .RequireRateLimiting(AuthEndpoints.SignInPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        mine.MapPost("/recovery-codes", async (TwoFactorConfirmationRequest request, TwoFactorManagement twoFactor, CancellationToken ct) =>
                new RecoveryCodesResponse(await twoFactor.RegenerateRecoveryCodesAsync(request.Confirmation, ct)))
            .WithName("RegenerateRecoveryCodes")
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapPost("/disable", async (TwoFactorConfirmationRequest request, TwoFactorManagement twoFactor, CancellationToken ct) =>
            {
                await twoFactor.DisableMineAsync(request.Confirmation, ct);
                return TypedResults.NoContent();
            })
            .WithName("DisableTwoFactor")
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapDelete("/api/users/{id:guid}/two-factor", async (Guid id, TwoFactorManagement twoFactor, CancellationToken ct) =>
            {
                await twoFactor.ResetForUserAsync(UserId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("ResetUserTwoFactor")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var settings = app.MapGroup("/api/settings/sign-in")
            .WithTags("Settings")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapGet("/", async (SignInSettingsService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(ct)))
            .WithName("GetSignInSettings");

        settings.MapPut("/", async (SignInSettingsRequest request, SignInSettingsService service, CancellationToken ct) =>
                ToResponse(await service.UpdateAsync(
                    new SignInSettings(request.RequireTwoFactorForAdministrators, request.RequireTwoFactorAfterDiscord), request.Confirmation, ct)))
            .WithName("UpdateSignInSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static TwoFactorStatusResponse ToResponse(TwoFactorStatus status) =>
        new(status.Enabled, status.RecoveryCodesLeft, status.Required);

    private static SignInSettingsResponse ToResponse(Application.Settings.ResolvedSettings settings)
    {
        var administrators = settings[SignInSettingsSection.RequireTwoFactorForAdministratorsField];
        var discord = settings[SignInSettingsSection.RequireTwoFactorAfterDiscordField];
        return new SignInSettingsResponse(administrators.Value == "true", administrators.IsLocked, discord.Value == "true", discord.IsLocked);
    }
}
