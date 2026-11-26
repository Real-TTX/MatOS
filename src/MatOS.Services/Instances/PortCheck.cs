using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MatOS.Services.Instances;

/// <summary>
/// One port of the port check, per address family: <c>open</c> (a connection got through), <c>closed</c> (refused: the router or the
/// host said no), <c>filtered</c> (no answer within the timeout: usually not forwarded), null for a family that was not checked.
/// <c>Web</c> is <c>caddy</c> or <c>other</c> when a web server answered on 80/443.
/// </summary>
public sealed record PortResult(int Port, string? Ipv4, string? Ipv6, string? Web);

/// <summary>The answer of <c>POST /api/v1/ports/check</c> (docs/SERVICES.md "Check my ports").</summary>
public sealed record PortsResult(string? Ipv4, string? Ipv6, DateTime CheckedAt, IReadOnlyList<PortResult> Ports);

/// <summary>
/// "Does my router forward the port?" for anybody: a TCP connect to the caller's own address, and for 80 and 443 a look at the web
/// server that answers. The callers make sure the address is the client's own and a public one (never an address from the request), so
/// the service cannot be turned into a scanner of other hosts or of server1's own network.
/// </summary>
public sealed class PortCheck(TimeProvider time, ILogger<PortCheck> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>The ports that are always checked, first; the person may add one.</summary>
    public static readonly int[] Standard = [80, 443];

    /// <summary>Where a probe really connects; the tests send it to local servers instead of the address.</summary>
    internal Func<IPEndPoint, IPEndPoint> Target { get; init; } = endPoint => endPoint;

    /// <summary>Checks 80, 443 and <paramref name="extra"/> of <paramref name="address"/> at once. The address is not checked here.</summary>
    public async Task<PortsResult> CheckAsync(IPAddress address, int? extra, CancellationToken cancellationToken)
    {
        int[] ports = extra is { } port && !Standard.Contains(port) ? [.. Standard, port] : Standard;
        var probes = await Task.WhenAll(ports.Select(p => ProbeAsync(address, p, cancellationToken)));
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var results = ports
            .Select((p, i) => new PortResult(p, v6 ? null : probes[i].State, v6 ? probes[i].State : null, probes[i].Web))
            .ToList();
        return new PortsResult(v6 ? null : address.ToString(), v6 ? address.ToString() : null, time.GetUtcNow().UtcDateTime, results);
    }

    /// <summary>The plain words for one state, as the page shows them.</summary>
    public static string Words(string? state, string? web) => state switch
    {
        "open" => web switch
        {
            "caddy" => "Open: your router forwards it, and a web server answers: Caddy (what MatOS uses).",
            "other" => "Open: your router forwards it, and a web server answers (not Caddy).",
            _ => "Open: your router forwards it.",
        },
        "closed" => "Closed: the connection was refused. Nothing is forwarded to a program that listens.",
        "filtered" => "Closed or not forwarded: nothing answered within 3 seconds.",
        _ => "Not checked.",
    };

    private async Task<(string State, string? Web)> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var target = Target(new IPEndPoint(address, port));
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(target, timeout.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ("closed", null);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug(ex, "Port {Port} of {Address} did not answer", port, address);
            return ("filtered", null);
        }

        if (port is not (80 or 443))
        {
            return ("open", null);
        }

        string? server = null;
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            if (port == 443)
            {
                await using var tls = new SslStream(stream, leaveInnerStreamOpen: true, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address.ToString() }, timeout.Token);
                server = await ReadServerAsync(tls, address, timeout.Token);
            }
            else
            {
                server = await ReadServerAsync(stream, address, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or System.Security.Authentication.AuthenticationException)
        {
            logger.LogDebug(ex, "No web server on port {Port} of {Address}", port, address);
        }

        return ("open", server is null ? null : server.Contains("Caddy", StringComparison.OrdinalIgnoreCase) ? "caddy" : "other");
    }

    /// <summary>The <c>Server</c> header of the answer to <c>GET /</c> (empty when it has none); null when nothing HTTP came back.</summary>
    private static async Task<string?> ReadServerAsync(Stream stream, IPAddress address, CancellationToken cancellationToken)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: MatOS-Services-Check\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var lines = Encoding.Latin1.GetString(buffer, 0, length).Split("\r\n");
        if (!lines[0].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return null;
        }

        return lines.FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase))?[7..].Trim() ?? "";
    }
}

/// <summary>
/// How often the port check may run: per caller address (an IPv4 address, an IPv6 /64), or per installation when it signs in with its
/// key, in a window of ten minutes; beyond that the answer names the seconds until the oldest check of the window drops out.
/// </summary>
public sealed class PortCheckLimits(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int PerAddress = 6;
    public const int PerInstance = 20;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _checks = new(StringComparer.Ordinal);

    /// <summary>Counts a check for <paramref name="key"/>; false and the seconds to wait when the limit is reached.</summary>
    public bool TryTake(string key, int limit, out int retryAfterSeconds)
    {
        var now = time.GetUtcNow();
        var queue = _checks.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((queue.Peek() + Window - now).TotalSeconds));
                return false;
            }

            queue.Enqueue(now);
        }

        retryAfterSeconds = 0;
        if (_checks.Count > 10_000)
        {
            // Old keys go now and then, so a flood of addresses cannot grow the table for ever.
            foreach (var (k, q) in _checks)
            {
                lock (q)
                {
                    if (q.Count == 0 || now - q.Last() >= Window)
                    {
                        _checks.TryRemove(k, out _);
                    }
                }
            }
        }

        return true;
    }
}
