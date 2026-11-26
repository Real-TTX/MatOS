using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MatOS.Services.Tunnel;

/// <summary>
/// The connected tunnels by installation name. One control connection per installation: a new one replaces the old, because the old
/// one is most likely a connection whose end MatOS has not noticed (a router that dropped it), and the installation must win.
/// </summary>
public sealed class TunnelRegistry(TimeProvider time, ILogger<TunnelRegistry> logger)
{
    private readonly ConcurrentDictionary<string, TunnelSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public TunnelSession Connect(string name, WebSocket control)
    {
        var session = new TunnelSession(name, control, time);
        TunnelSession? replaced = null;
        _sessions.AddOrUpdate(name, session, (_, old) =>
        {
            replaced = old;
            return session;
        });

        if (replaced is not null)
        {
            logger.LogInformation("Tunnel of {Name} replaced by a new connection", name);
            _ = replaced.CloseAsync("replaced by a new connection");
        }

        return session;
    }

    public TunnelSession? Get(string name) => _sessions.TryGetValue(name, out var session) ? session : null;

    /// <summary>Removes the session only if it is still the current one (a replaced session must not remove its successor).</summary>
    public void Disconnect(TunnelSession session) => _sessions.TryRemove(new KeyValuePair<string, TunnelSession>(session.Name, session));

    /// <summary>Closes the tunnel of a name that was given back.</summary>
    public async Task CloseAsync(string name)
    {
        if (_sessions.TryRemove(name, out var session))
        {
            await session.CloseAsync("the name was given back");
        }
    }

    public IReadOnlyCollection<TunnelSession> Sessions => _sessions.Values.ToList();
}
