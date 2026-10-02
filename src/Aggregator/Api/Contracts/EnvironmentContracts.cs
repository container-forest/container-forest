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
//
// {container} is a full id, an exact name or a unique id prefix, as in the docker CLI.
// An environment the user may not see is 404, the same as a missing one. Actions return once the agent has
// done them: 409 when the action makes no sense in the container's state (or the id prefix is ambiguous),
// 503 when the agent is offline (nothing is queued), 504 when it did not answer in time.

/// <param name="IsOnline">Whether the environment's agent is connected right now.</param>
/// <param name="Role">The user's role on the environment as a whole; null for administrators and for users who see it only through stack roles.</param>
public sealed record EnvironmentResponse(
    Guid Id,
    string Name,
    bool IsPrivate,
    bool IsEnrolled,
    bool IsOnline,
    string? AgentHostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    Role? Role);

/// <param name="StackName">The compose project; null for a container outside compose.</param>
/// <param name="StatusText">Docker's human-readable status, e.g. "Up 3 minutes (healthy)".</param>
/// <param name="CanControl">Whether the user may start, stop and restart it; for hiding buttons only, every action is checked again.</param>
public sealed record ContainerResponse(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    DateTimeOffset? CreatedAt,
    bool CanControl);
