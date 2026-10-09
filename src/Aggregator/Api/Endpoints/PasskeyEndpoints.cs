using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Passkeys: signing in with one and managing them, see PasskeyContracts.</summary>
internal static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Account");

        auth.MapGet("/methods", (DiscordAuthOptions discord) => new SignInMethodsResponse(Password: true, Passkey: true, Discord: discord.IsConfigured))
            .WithName("GetSignInMethods");

        auth.MapPost("/passkey/options", async (PasskeySignIn signIn) => Options(await signIn.MakeRequestOptionsAsync()))
            .WithName("GetPasskeyRequestOptions")
            .Produces(StatusCodes.Status200OK, contentType: "application/json");

        auth.MapPost("/passkey/login", async Task<Results<Ok<MeResponse>, ProblemHttpResult>> (
                PasskeyLoginRequest request, PasskeySignIn signIn, CancellationToken ct) =>
            {
                var user = await signIn.SignInAsync(request.Credential, request.RememberMe, ct);
                return user is null
                    ? TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: "The passkey was not accepted.")
                    : TypedResults.Ok(new MeResponse(user.Id.Value, user.UserName, user.IsAdmin));
            })
            .WithName("PasskeyLogin")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var mine = app.MapGroup("/api/me/passkeys")
            .WithTags("Account")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mine.MapPost("/options", async (PasskeyManagement passkeys, CancellationToken ct) =>
                Options(await passkeys.MakeCreationOptionsAsync(ct)))
            .WithName("GetPasskeyCreationOptions")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapGet("/", async (PasskeyManagement passkeys, CancellationToken ct) =>
                (await passkeys.ListMineAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListPasskeys");

        mine.MapPost("/", async (AddPasskeyRequest request, PasskeyManagement passkeys, CancellationToken ct) =>
            {
                var added = await passkeys.AddAsync(request.Credential, request.Name, ct);
                return TypedResults.Created($"/api/me/passkeys/{added.Id}", ToResponse(added));
            })
            .WithName("AddPasskey")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mine.MapPut("/{id}", async (string id, RenamePasskeyRequest request, PasskeyManagement passkeys, CancellationToken ct) =>
                ToResponse(await passkeys.RenameAsync(id, request.Name, ct)))
            .WithName("RenamePasskey")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mine.MapDelete("/{id}", async (string id, PasskeyManagement passkeys, CancellationToken ct) =>
            {
                await passkeys.RemoveMineAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("RemovePasskey")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var users = app.MapGroup("/api/users/{userId:guid}/passkeys")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/", async (Guid userId, PasskeyManagement passkeys, CancellationToken ct) =>
                (await passkeys.ListForUserAsync(UserId.From(userId), ct)).Select(ToResponse).ToList())
            .WithName("ListUserPasskeys");

        users.MapDelete("/{id}", async (Guid userId, string id, PasskeyManagement passkeys, CancellationToken ct) =>
            {
                await passkeys.RemoveForUserAsync(UserId.From(userId), id, ct);
                return TypedResults.NoContent();
            })
            .WithName("RemoveUserPasskey");

        return app;
    }

    // The options are WebAuthn JSON made by Identity; they go to the browser as they are.
    private static IResult Options(string json) => TypedResults.Text(json, "application/json");

    private static PasskeyResponse ToResponse(PasskeyView passkey) =>
        new(passkey.Id, passkey.Name, passkey.CreatedAt, passkey.IsBackedUp, passkey.Transports);
}
