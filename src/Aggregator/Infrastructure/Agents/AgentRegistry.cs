using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>Live sessions, at most one per environment.</summary>
public sealed class AgentRegistry(ILogger<AgentRegistry> logger)
{
    private readonly ConcurrentDictionary<EnvironmentId, AgentSession> sessions = new();

    /// <summary>Registers a new session. An existing session of the same agent is closed: the newest connection wins.</summary>
    public void Open(AgentSession session)
    {
        while (true)
        {
            if (sessions.TryGetValue(session.EnvironmentId, out var existing))
            {
                if (sessions.TryUpdate(session.EnvironmentId, session, existing))
                {
                    logger.LogWarning(
                        "Environment {EnvironmentId} connected again; closing the previous session {SessionId}",
                        session.EnvironmentId, existing.SessionId);
                    existing.Close("replaced by a new connection");
                    return;
                }
            }
            else if (sessions.TryAdd(session.EnvironmentId, session))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Closes the session and unregisters it. Returns false when a newer session had already replaced it,
    /// so the caller must not record a disconnect for the environment.
    /// </summary>
    public bool Remove(AgentSession session, string reason)
    {
        session.Close(reason);
        return sessions.TryRemove(KeyValuePair.Create(session.EnvironmentId, session));
    }

    public bool TryGet(EnvironmentId environmentId, [NotNullWhen(true)] out AgentSession? session) =>
        sessions.TryGetValue(environmentId, out session) && !session.IsClosed;

    public bool IsOnline(EnvironmentId environmentId) => TryGet(environmentId, out _);
}
