using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Temporary endpoints to create users and environment roles for trying out authorization locally.
/// Real user management and login belong to the panel API. Mapped only in the Development environment.
/// </summary>
internal static class DevAccessEndpoints
{
    public static IEndpointRouteBuilder MapDevAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev").WithTags("Dev: access");

        group.MapPost("/users", async (
            CreateUserRequest request, IUserRepository users, IUnitOfWork unitOfWork, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var user = request.IsAdmin ? User.CreateAdministrator(request.UserName, now) : User.Create(request.UserName, now);
            if (await users.FindByUserNameAsync(user.UserName, ct) is not null)
            {
                throw new DomainConflictException($"User '{user.UserName}' already exists.");
            }

            users.Add(user);
            await unitOfWork.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/dev/users/{user.Id}", new UserResponse(user.Id.Value, user.UserName, user.IsAdmin));
        });

        group.MapPost("/users/{userId:guid}/environment-roles", async (
            Guid userId,
            GrantEnvironmentRoleRequest request,
            IUserRepository users,
            IRoleAssignmentRepository roleAssignments,
            IUnitOfWork unitOfWork,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var user = await users.GetAsync(UserId.From(userId), ct);
            roleAssignments.Add(RoleAssignment.ForEnvironment(
                user.Id, EnvironmentId.From(request.EnvironmentId), request.Role, time.GetUtcNow()));
            await unitOfWork.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    public sealed record CreateUserRequest(string UserName, bool IsAdmin);

    public sealed record UserResponse(Guid Id, string UserName, bool IsAdmin);

    public sealed record GrantEnvironmentRoleRequest(Guid EnvironmentId, Role Role);
}
