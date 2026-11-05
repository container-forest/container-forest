using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Api.Contracts;

// Environments and their containers for the panel, limited to what the signed-in user may see.
//
//   GET  /api/environments                                       -> EnvironmentResponse[] (name order)
//   GET  /api/environments/{id}/containers                       -> ContainerResponse[]; asks the agent
//   POST /api/environments/{id}/containers/{container}/start     -> 204
//   POST /api/environments/{id}/containers/{container}/stop      -> 204; ?timeoutSeconds=0..300, default 10
//   POST /api/environments/{id}/containers/{container}/restart   -> 204; ?timeoutSeconds=0..300, default 10
//   GET  /api/environments/{id}/containers/{container}/logs      -> ContainerLogsResponse; ?tail=1..5000 (default 200), ?since=<ISO 8601>
//
// {container} is a full id, an exact name or a unique id prefix, as in the docker CLI.
// An environment the user may not see is 404, the same as a missing one. Actions return once the agent has
// done them: 409 when the action makes no sense in the container's state (or the id prefix is ambiguous),
// 503 when the agent is offline (nothing is queued), 504 when it did not answer in time.
// Logs are read once, without following: the panel asks again for newer lines (since = the last line's time).
//
// Administrators only:
//   POST   /api/environments                                       -> 201 EnvironmentCreatedResponse with the first enrollment token
//   DELETE /api/environments/{id}                                  -> 204; only without stacks
//
// Administrators and the environment's administrators (role EnvironmentAdmin, ADR-0012; canAdminister in the response):
//   PUT    /api/environments/{id}                                  -> EnvironmentResponse; name and privacy
//   POST   /api/environments/{id}/enrollment-token                 -> EnrollmentTokenResponse; the previous token stops working
//   POST   /api/environments/{id}/revoke-agent                     -> 204; forgets the agent's credentials and drops its connection
//   GET    /api/environments/{id}/registry-credentials             -> RegistryCredentialResponse[] (server order); never the password
//   PUT    /api/environments/{id}/registry-credentials             -> 201 or 200 RegistryCredentialResponse; by server, replaces its login
//   DELETE /api/environments/{id}/registry-credentials/{credential} -> 204
//   GET    /api/environments/{id}/role-assignments                 -> EnvironmentRoleResponse[]; roles on the environment and its stacks
//   POST   /api/environments/{id}/role-assignments                 -> 201 EnvironmentRoleResponse; 409 when the user has a role on that scope
//   PUT    /api/environments/{id}/role-assignments/{assignment}    -> EnvironmentRoleResponse
//   DELETE /api/environments/{id}/role-assignments/{assignment}    -> 204
// The EnvironmentAdmin role itself is given, changed and taken by administrators only (403 for anyone else).
//
// The enrollment token is shown once, in the answer that issues it: the aggregator keeps only its hash.

/// <param name="IsOnline">Whether the environment's agent is connected right now.</param>
/// <param name="EnrollmentTokenExpiresAt">Until when an issued enrollment token is valid; null when there is none, and for non-administrators.</param>
/// <param name="Role">The user's role on the environment as a whole; null for administrators and for users who see it only through stack roles.</param>
/// <param name="CanAdminister">Whether the user administers the environment (administrator or its EnvironmentAdmin); for showing controls only.</param>
public sealed record EnvironmentResponse(
    Guid Id,
    string Name,
    bool IsPrivate,
    bool IsEnrolled,
    bool IsOnline,
    string? AgentHostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    Role? Role,
    DateTimeOffset? EnrollmentTokenExpiresAt,
    bool CanAdminister);

/// <param name="StackName">The compose project; null for a container outside compose.</param>
/// <param name="StatusText">Docker's human-readable status, e.g. "Up 3 minutes (healthy)".</param>
/// <param name="CanControl">Whether the user may start, stop and restart it; for hiding buttons only, every action is checked again.</param>
/// <param name="IsSystem">A part of container-forest itself (the aggregator, the panel, an agent): it can only be started.</param>
public sealed record ContainerResponse(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    DateTimeOffset? CreatedAt,
    bool CanControl,
    bool IsSystem);

/// <param name="Lines">Oldest first.</param>
public sealed record ContainerLogsResponse(IReadOnlyList<ContainerLogLineResponse> Lines);

/// <param name="At">When Docker received the line; null when the agent did not report it.</param>
public sealed record ContainerLogLineResponse(DateTimeOffset? At, LogStream Stream, string Text);

/// <param name="IsPrivate">Visible only to users with a role on it or on one of its stacks.</param>
public sealed record CreateEnvironmentRequest(string Name, bool IsPrivate);

public sealed record UpdateEnvironmentRequest(string Name, bool IsPrivate);

/// <param name="EnrollmentToken">Shown only here: the agent's first start takes it (CF_AGENT__ENROLLMENTTOKEN).</param>
public sealed record EnvironmentCreatedResponse(Guid Id, string Name, string EnrollmentToken, DateTimeOffset ExpiresAt);

/// <param name="EnrollmentToken">Shown only here.</param>
public sealed record EnrollmentTokenResponse(string EnrollmentToken, DateTimeOffset ExpiresAt);

/// <param name="Server">Registry host, e.g. "ghcr.io", "registry.example.com:5000" or "docker.io".</param>
/// <param name="Password">Password or access token with read access to the registry; never returned.</param>
public sealed record SetRegistryCredentialRequest(string Server, string Username, string Password);

public sealed record RegistryCredentialResponse(Guid Id, string Server, string Username, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="StackId">Null for a role on the whole environment.</param>
public sealed record EnvironmentRoleResponse(
    Guid Id, Guid UserId, string UserName, Guid? StackId, string? StackName, Role Role, DateTimeOffset GrantedAt);

/// <param name="UserName">An existing user; users are created by administrators.</param>
/// <param name="StackId">Null for a role on the whole environment.</param>
public sealed record GrantEnvironmentRoleRequest(string UserName, Guid? StackId, Role Role);

public sealed record ChangeEnvironmentRoleRequest(Role Role);
