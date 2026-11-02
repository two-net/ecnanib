using Microsoft.AspNetCore.SignalR;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// The hub connections and circuits of each session, which is what lets revoking one reach a live
/// circuit. It's in memory because they are too, and there's one instance (ASM-06). A session's entry
/// stays until the session ends, which the absolute timeout bounds. See docs/architecture.md.
/// </summary>
internal sealed class SessionConnections
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, LiveSession> _sessions = [];

    public void Add(Guid sessionId, HubCallerContext connection)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new LiveSession();
                _sessions.Add(sessionId, session);
            }

            session.Connections.Add(connection);
        }
    }

    public void Remove(Guid sessionId, HubCallerContext connection)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.Connections.Remove(connection);
            }
        }
    }

    /// <summary>
    /// Does nothing once the session has ended, and the circuit then can't be rejoined. A connection
    /// is added before its first call, so a session with one always has an entry until it ends.
    /// </summary>
    public void AddCircuit(Guid sessionId, string secret)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.Circuits.Add(secret);
            }
        }
    }

    public bool OwnsCircuit(Guid sessionId, string secret)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) && session.Circuits.Contains(secret);
        }
    }

    /// <summary>
    /// Call it after the row is gone, so that a client reconnecting at once finds its cookie refused.
    /// Nothing can rejoin these circuits afterwards, so the framework evicts them.
    /// </summary>
    public void End(IEnumerable<Guid> sessionIds)
    {
        List<HubCallerContext> connections = [];

        lock (_lock)
        {
            foreach (var sessionId in sessionIds)
            {
                if (_sessions.Remove(sessionId, out var session))
                {
                    connections.AddRange(session.Connections);
                }
            }
        }

        foreach (var connection in connections)
        {
            connection.Abort();
        }
    }

    private sealed class LiveSession
    {
        public HashSet<HubCallerContext> Connections { get; } = [];

        /// <summary>Their secrets, because that's what the client presents to rejoin one.</summary>
        public HashSet<string> Circuits { get; } = new(StringComparer.Ordinal);
    }
}
