using System.Collections.Concurrent;
using System.Net.WebSockets;
using MatOS.Services.Data;

namespace MatOS.Services.Tunnel;

/// <summary>
/// The connected tunnels by installation name. One control connection per installation: a new one replaces the old, because the old
/// one is most likely a connection whose end MatOS has not noticed (a router that dropped it), and the installation must win.
/// </summary>
public sealed class TunnelRegistry(TimeProvider time, ILogger<TunnelRegistry> logger)
{
    private readonly ConcurrentDictionary<string, TunnelSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <param name="remote">The address the installation connected from (shown in the admin area).</param>
    /// <param name="version">The MatOS version it named (the statistics of its sessions).</param>
    public TunnelSession Connect(string name, WebSocket control, string? remote = null, string? version = null)
    {
        var session = new TunnelSession(name, control, time) { RemoteAddress = remote, Version = version };
        TunnelSession? replaced = null;
        _sessions.AddOrUpdate(name, session, (_, old) =>
        {
            replaced = old;
            return session;
        });

        if (replaced is not null)
        {
            logger.LogInformation("Tunnel of {Name} replaced by a new connection", name);
            _ = replaced.CloseAsync(TunnelCloseReason.Replaced);
        }

        return session;
    }

    public TunnelSession? Get(string name) => _sessions.TryGetValue(name, out var session) ? session : null;

    /// <summary>Removes the session only if it is still the current one (a replaced session must not remove its successor).</summary>
    public void Disconnect(TunnelSession session) => _sessions.TryRemove(new KeyValuePair<string, TunnelSession>(session.Name, session));

    /// <summary>Closes the tunnel of a name (given back, blocked, ended by an administrator); false when none was connected.</summary>
    public async Task<bool> CloseAsync(string name, TunnelCloseReason reason = TunnelCloseReason.GivenBack)
    {
        if (!_sessions.TryRemove(name, out var session))
        {
            return false;
        }

        await session.CloseAsync(reason);
        return true;
    }

    public IReadOnlyCollection<TunnelSession> Sessions => _sessions.Values.ToList();
}
