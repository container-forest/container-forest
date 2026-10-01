using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>User management for administrators (see UserContracts).</summary>
internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", async (UserManagementService service, CancellationToken ct) =>
                (await service.ListAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListUsers");

        group.MapGet("/{id:guid}", async (Guid id, UserManagementService service, CancellationToken ct) =>
            {
                var details = await service.GetAsync(UserId.From(id), ct);
                return new UserDetailsResponse(ToResponse(details.User), details.RoleAssignments.Select(ToResponse).ToList());
            })
            .WithName("GetUser")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (CreateUserRequest request, UserManagementService service, CancellationToken ct) =>
            {
                var user = await service.CreateAsync(request.UserName, request.Password, request.IsAdmin, ct);
                return TypedResults.Created($"/api/users/{user.User.Id}", ToResponse(user));
            })
            .WithName("CreateUser")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/user-name", async (Guid id, RenameUserRequest request, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.RenameAsync(UserId.From(id), request.UserName, ct)))
            .WithName("RenameUser")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.DeactivateAsync(UserId.From(id), ct)))
            .WithName("DeactivateUser")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/activate", async (Guid id, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.ActivateAsync(UserId.From(id), ct)))
            .WithName("ActivateUser")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/admin", async (Guid id, SetAdminRequest request, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.SetAdminAsync(UserId.From(id), request.IsAdmin, ct)))
            .WithName("SetUserAdmin")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/password", async (Guid id, ResetPasswordRequest request, UserManagementService service, CancellationToken ct) =>
            {
                await service.ResetPasswordAsync(UserId.From(id), request.NewPassword, ct);
                return TypedResults.NoContent();
            })
            .WithName("ResetUserPassword")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/unlock", async (Guid id, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.UnlockAsync(UserId.From(id), ct)))
            .WithName("UnlockUser")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/role-assignments", async (Guid id, GrantRoleRequest request, UserManagementService service, CancellationToken ct) =>
            {
                var assignment = await service.GrantRoleAsync(
                    UserId.From(id),
                    EnvironmentId.From(request.EnvironmentId),
                    request.StackId is { } stackId ? StackId.From(stackId) : null,
                    request.Role,
                    ct);
                return TypedResults.Created($"/api/users/{id}/role-assignments/{assignment.Id}", ToResponse(assignment));
            })
            .WithName("GrantRole")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/role-assignments/{assignmentId:guid}", async (
                Guid id, Guid assignmentId, ChangeRoleRequest request, UserManagementService service, CancellationToken ct) =>
                ToResponse(await service.ChangeRoleAsync(UserId.From(id), RoleAssignmentId.From(assignmentId), request.Role, ct)))
            .WithName("ChangeRole")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}/role-assignments/{assignmentId:guid}", async (
                Guid id, Guid assignmentId, UserManagementService service, CancellationToken ct) =>
            {
                await service.RevokeRoleAsync(UserId.From(id), RoleAssignmentId.From(assignmentId), ct);
                return TypedResults.NoContent();
            })
            .WithName("RevokeRole")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static UserResponse ToResponse(UserSummary summary) => new(
        summary.User.Id.Value,
        summary.User.UserName,
        summary.User.IsAdmin,
        summary.User.IsActive,
        summary.User.CreatedAt,
        summary.LockedOutUntil);

    private static RoleAssignmentResponse ToResponse(RoleAssignmentView view) => new(
        view.Id.Value,
        view.StackId is null ? RoleScope.Environment : RoleScope.Stack,
        view.EnvironmentId.Value,
        view.EnvironmentName,
        view.StackId?.Value,
        view.StackName,
        view.Role,
        view.GrantedAt);
}
