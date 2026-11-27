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
    private readonly Dictionary<string, Streams> _streams = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The lease of a visitor's stream of <paramref name="name"/> while it is piped: its token is cancelled by <see cref="EndStreams"/>. A stream
    /// outlives the control connection it was opened on (a new connection replaces the old one), so the streams are kept by name, not by session.
    /// </summary>
    public StreamLease LeaseStream(string name)
    {
        lock (_streams)
        {
            if (!_streams.TryGetValue(name, out var streams))
            {
                streams = new Streams();
                _streams[name] = streams;
            }

            streams.Count++;
            return new StreamLease(this, name, streams);
        }
    }

    /// <summary>Ends every visitor's stream of <paramref name="name"/> that is still piped (the name was blocked); returns how many.</summary>
    public int EndStreams(string name)
    {
        Streams? streams;
        int count;
        lock (_streams)
        {
            if (!_streams.Remove(name, out streams))
            {
                return 0;
            }

            count = streams.Count;
        }

        streams.Ended.Cancel();
        return count;
    }

    private void Release(string name, Streams streams)
    {
        lock (_streams)
        {
            if (--streams.Count > 0)
            {
                return;
            }

            // Not disposed: EndStreams may cancel it right after it left the table (a source without a timer needs no disposing).
            if (_streams.TryGetValue(name, out var current) && current == streams)
            {
                _streams.Remove(name);
            }
        }
    }

    internal sealed class Streams
    {
        public CancellationTokenSource Ended { get; } = new();

        public int Count { get; set; }
    }

    /// <summary>One piped stream of a name (<see cref="LeaseStream"/>); dispose it when the pipe has ended.</summary>
    public sealed class StreamLease : IDisposable
    {
        private readonly TunnelRegistry _registry;
        private readonly string _name;
        private readonly Streams _streams;
        private int _disposed;

        internal StreamLease(TunnelRegistry registry, string name, Streams streams)
        {
            _registry = registry;
            _name = name;
            _streams = streams;
            Ended = streams.Ended.Token;
        }

        /// <summary>Cancelled when the name's streams must end (blocked).</summary>
        public CancellationToken Ended { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registry.Release(_name, _streams);
            }
        }
    }
}
