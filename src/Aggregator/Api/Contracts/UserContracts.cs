using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Api.Contracts;

// User management for the panel: every endpoint below is for administrators only (403 for anyone else).
//
//   GET    /api/users                               -> UserResponse[] (by user name)
//   GET    /api/users/{id}                          -> UserDetailsResponse
//   POST   /api/users                               CreateUserRequest -> 201 UserResponse
//   PUT    /api/users/{id}/user-name                RenameUserRequest -> 200 UserResponse
//   POST   /api/users/{id}/deactivate               -> 200 UserResponse
//   POST   /api/users/{id}/activate                 -> 200 UserResponse
//   PUT    /api/users/{id}/admin                    SetAdminRequest -> 200 UserResponse
//   POST   /api/users/{id}/password                 ResetPasswordRequest -> 204
//   POST   /api/users/{id}/unlock                   -> 200 UserResponse
//   POST   /api/users/{id}/role-assignments         GrantRoleRequest -> 201 RoleAssignmentResponse
//   PUT    /api/users/{id}/role-assignments/{aid}   ChangeRoleRequest -> 200 RoleAssignmentResponse
//   DELETE /api/users/{id}/role-assignments/{aid}   -> 204
//
// Common rules:
// - Unknown user, assignment, environment or stack: 404. Duplicate user name: 409 (names are case-insensitive).
// - The last active administrator cannot be deactivated or lose the admin flag: 409. Otherwise nobody could manage users.
// - Deactivating a user, resetting their password or revoking admin ends their existing sessions.
// - Passwords: at least 12 characters, no rules about character classes (NIST SP 800-63B). A password that
//   fails the policy is 400 with the reasons in the problem details. Passwords are never logged or returned.

/// <param name="LockedOutUntil">Set while the account is locked after too many failed logins; null otherwise.</param>
public sealed record UserResponse(
    Guid Id,
    string UserName,
    bool IsAdmin,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LockedOutUntil,
    bool TwoFactorEnabled = false);

/// <param name="RoleAssignments">Ordered by environment name, then stack name.</param>
public sealed record UserDetailsResponse(UserResponse User, IReadOnlyList<RoleAssignmentResponse> RoleAssignments);

/// <param name="Password">Initial password; the administrator hands it over and the user changes it.</param>
public sealed record CreateUserRequest(string UserName, string Password, bool IsAdmin);

public sealed record RenameUserRequest(string UserName);

public sealed record SetAdminRequest(bool IsAdmin);

public sealed record ResetPasswordRequest(string NewPassword);

public enum RoleScope
{
    /// <summary>The role applies to the whole environment and all its stacks.</summary>
    Environment,

    /// <summary>The role applies to one stack and makes its environment visible.</summary>
    Stack,
}

/// <param name="StackId">Null for <see cref="RoleScope.Environment"/>.</param>
/// <param name="StackName">Null for <see cref="RoleScope.Environment"/>.</param>
public sealed record RoleAssignmentResponse(
    Guid Id,
    RoleScope Scope,
    Guid EnvironmentId,
    string EnvironmentName,
    Guid? StackId,
    string? StackName,
    Role Role,
    DateTimeOffset GrantedAt);

/// <summary>
/// Grants a role on an environment (<paramref name="StackId"/> null) or on one of its stacks.
/// A user has at most one role per scope: granting on a scope that already has one is 409, change it instead.
/// </summary>
public sealed record GrantRoleRequest(Guid EnvironmentId, Guid? StackId, Role Role);

public sealed record ChangeRoleRequest(Role Role);
