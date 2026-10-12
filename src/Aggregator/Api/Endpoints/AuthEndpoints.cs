using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Password sign-in for the panel: the <c>cf_session</c> cookie (see AccountContracts).</summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Account");

        group.MapPost("/login", async Task<Results<Ok<MeResponse>, ProblemHttpResult>> (
                LoginRequest request, PasswordSignIn signIn, CancellationToken ct) =>
            {
                var user = await signIn.SignInAsync(request.UserName, request.Password, request.RememberMe, ct);
                return user is null
                    ? TypedResults.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Unauthorized",
                        detail: "Wrong user name or password.")
                    : TypedResults.Ok(new MeResponse(user.Id.Value, user.UserName, user.IsAdmin));
            })
            .WithName("Login")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}
