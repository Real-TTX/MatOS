using System.Net;
using System.Net.Sockets;
using System.Text;
using MatOS.Services.Configuration;
using MatOS.Services.Instances;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.WebUtilities;

namespace MatOS.Services.Edge;

/// <summary>
/// The edge: two TCP listeners (TLS on 8443 by SNI, plain HTTP on 8081 by Host) that pass a visitor's connection, unread and undecrypted,
/// into the tunnel of the installation its host name belongs to. Names outside the zone go to the configured fallback (the existing proxy
/// of server1), so the edge can sit in front of it on 80/443.
/// <para>
/// TLS is routed per connection (the SNI cannot change). Plain HTTP is routed per request: a proxy in front of the edge reuses its
/// connections for any host, so every request on such a connection passes <see cref="HttpRequestGate"/>, which ends the connection
/// at the first request for another destination.
/// </para>
/// </summary>
public sealed class EdgeService(
    ServicesOptions options, TunnelRegistry registry, InstanceDirectory directory, CustomDomains domains, UsageRecorder usage,
    ILogger<EdgeService> logger)
    : BackgroundService
{
    /// <summary>How long a visitor may take to send the ClientHello or the request head: a slow client must not hold a slot.</summary>
    private static readonly TimeSpan PeekTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    // Route keys that no name can be ('#' is not allowed in a name).
    private const string FallbackRoute = "#fallback";
    private const string NowhereRoute = "#nowhere";

    private readonly AddressLimits _addresses = new(options.Edge.MaxConnectionsPerAddress, options.Edge.NewConnectionsPerAddressPerMinute);

    private Socket? _https;
    private Socket? _http;
    private int _pending;
    private int _tunnels;
    private int _fallbacks;

    /// <summary>Where the TLS edge listens (the port is chosen by the system when configured as 0).</summary>
    public IPEndPoint? HttpsEndPoint => _https?.LocalEndPoint as IPEndPoint;

    public IPEndPoint? HttpEndPoint => _http?.LocalEndPoint as IPEndPoint;

    /// <summary>The visitors connected right now (shown to the administrator).</summary>
    public int Connections => Volatile.Read(ref _pending) + Volatile.Read(ref _tunnels) + Volatile.Read(ref _fallbacks);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Bound here and not in ExecuteAsync, so a port that is taken stops the start and the endpoints are known once started.
        _https = Listeners.Bind(options.ListenIp(), options.Edge.HttpsPort, SocketType.Stream);
        _https.Listen(512);
        _http = Listeners.Bind(options.ListenIp(), options.Edge.HttpPort, SocketType.Stream);
        _http.Listen(512);
        logger.LogInformation("Edge listening: TLS on {Https}, HTTP on {Http}", HttpsEndPoint, HttpEndPoint);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(AcceptAsync(_https!, tls: true, stoppingToken), AcceptAsync(_http!, tls: false, stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _https?.Dispose();
        _http?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _addresses.Dispose();
        base.Dispose();
    }

    private async Task AcceptAsync(Socket listener, bool tls, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Accept failed");
                continue;
            }

            _ = HandleAsync(client, tls, stoppingToken);
        }
    }

    private async Task HandleAsync(Socket client, bool tls, CancellationToken stoppingToken)
    {
        using var _ = client;
        if (Interlocked.Increment(ref _pending) > options.Edge.MaxPendingConnections)
        {
            // A flood of connections that send nothing: closed at once, an answer would only cost more.
            Interlocked.Decrement(ref _pending);
            return;
        }

        var pending = true;
        string? addressKey = null;
        try
        {
            client.NoDelay = true;
            var peer = (IPEndPoint)client.RemoteEndPoint!;
            var trustedPeer = options.IsTrustedProxy(peer.Address);
            var proxyProtocol = tls ? options.Edge.ProxyProtocol : options.Edge.HttpProxyProtocol;
            if (proxyProtocol && !trustedPeer)
            {
                // Only a trusted proxy may name the visitor; anybody else could pick the address the limits count.
                return;
            }

            // Without the PROXY protocol, a trusted proxy in front is the address of every visitor: limiting it would limit everybody.
            if (!proxyProtocol && !trustedPeer && !TryEnter(peer.Address, ref addressKey))
            {
                return;
            }

            var peek = await PeekAsync(client, tls, proxyProtocol, stoppingToken);
            if (peek.Result is PeekResult.Invalid or PeekResult.NeedMore)
            {
                return;
            }

            var visitor = peek.Visitor ?? peer;
            if (proxyProtocol && peek.Visitor is not null && !TryEnter(peek.Visitor.Address, ref addressKey))
            {
                return;
            }

            var route = Route(peek.Host);
            Interlocked.Decrement(ref _pending);
            pending = false;
            if (route == NowhereRoute)
            {
                if (!tls)
                {
                    await TryReplyAsync(client, 404, "No MatOS here", "There is no MatOS at this address.");
                }

                return;
            }

            if (route == FallbackRoute)
            {
                await FallbackAsync(client, tls, visitor, peek.Peeked, stoppingToken);
                return;
            }

            await TunnelAsync(client, tls, route, HostLabel(peek.Host!, route), visitor.Address, peek.Peeked, stoppingToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A visitor that went away; nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Edge connection failed");
        }
        finally
        {
            if (pending)
            {
                Interlocked.Decrement(ref _pending);
            }

            if (addressKey is not null)
            {
                _addresses.Leave(addressKey);
            }
        }
    }

    private bool TryEnter(IPAddress address, ref string? key)
    {
        var candidate = Networks.LimitKey(address);
        if (!_addresses.TryEnter(candidate))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Where a host goes: the installation's name, <see cref="FallbackRoute"/> (outside the zone, the zone itself, no host) or
    /// <see cref="NowhereRoute"/> (a name in the zone that no installation can hold, such as one level too deep). A host outside the zone
    /// goes to an installation when it is a verified domain of its own or a name below one (<see cref="CustomDomains"/>).
    /// </summary>
    private string Route(string? host)
    {
        if (host is null || host == options.Zone)
        {
            return FallbackRoute;
        }

        if (!HostNames.InZone(host, options.Zone))
        {
            return domains.Find(host) ?? FallbackRoute;
        }

        return HostNames.InstanceName(host, options.Zone) is { } name ? name : NowhereRoute;
    }

    /// <summary>
    /// Which of an installation's hosts a connection is for, as the statistics count it: empty for the installation itself, <c>app</c> for
    /// <c>app.anna.zone</c>, the domain for its own domain or a name below it. On a plain HTTP connection the first request decides.
    /// </summary>
    private string HostLabel(string host, string name)
    {
        if (!HostNames.InZone(host, options.Zone))
        {
            return domains.FindDomain(host) ?? "";
        }

        var rest = host[..^(options.Zone.Length + 1)];
        return rest == name ? "" : rest[..rest.IndexOf('.')];
    }

    private sealed record Peek(PeekResult Result, string? Host, ReadOnlyMemory<byte> Peeked, IPEndPoint? Visitor);

    /// <summary>
    /// Reads until the host name is known, the limit of 16 KiB is reached, or 5 s have passed; with <paramref name="proxyProtocol"/> the
    /// PROXY header comes first and names the visitor.
    /// </summary>
    private static async Task<Peek> PeekAsync(Socket client, bool tls, bool proxyProtocol, CancellationToken stoppingToken)
    {
        var invalid = new Peek(PeekResult.Invalid, null, default, null);
        var buffer = new byte[(tls ? SniParser.MaxBytes : HttpHostParser.MaxBytes) + (proxyProtocol ? ProxyProtocol.MaxBytes : 0)];
        var length = 0;
        var start = proxyProtocol ? -1 : 0;
        IPEndPoint? visitor = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(PeekTimeout);
        try
        {
            while (length < buffer.Length)
            {
                var read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token);
                if (read == 0)
                {
                    return invalid;
                }

                length += read;
                if (start < 0)
                {
                    var header = ProxyProtocol.TryRead(buffer.AsSpan(0, length), out var headerLength, out visitor);
                    if (header == PeekResult.NeedMore)
                    {
                        continue;
                    }

                    if (header != PeekResult.Found)
                    {
                        return invalid;
                    }

                    start = headerLength;
                    if (start == length)
                    {
                        continue;
                    }
                }

                var data = buffer.AsSpan(start, length - start);
                var result = tls ? SniParser.TryRead(data, out var host) : HttpHostParser.TryRead(data, out host);
                if (result != PeekResult.NeedMore)
                {
                    return new Peek(result, host, buffer.AsMemory(start, length - start), visitor);
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Too slow: the same as garbage.
        }

        return invalid;
    }

    /// <summary>The settings of a pipe; plain HTTP gets the gate that keeps every request on the connection's destination.</summary>
    private PipeSettings Pipe(bool tls, string route, IPipeMeter meter) =>
        new(TimeSpan.FromSeconds(options.Edge.IdleTimeoutSeconds), tls ? null : new HttpRequestGate(route, Route), meter);

    private async Task TunnelAsync(
        Socket client, bool tls, string name, string label, IPAddress visitor, ReadOnlyMemory<byte> peeked, CancellationToken stoppingToken)
    {
        if (Interlocked.Increment(ref _tunnels) > options.Edge.MaxConnections)
        {
            Interlocked.Decrement(ref _tunnels);
            if (await directory.FindAsync(name, stoppingToken) is not null)
            {
                // Only a name that exists gets a row: a made-up one is nobody's statistics.
                usage.Refuse(name, label, tls, UsageRefusal.Limit);
            }

            if (!tls)
            {
                await TryReplyAsync(client, 503, "Busy", "MatOS Services is busy. Please try again in a moment.");
            }

            return;
        }

        try
        {
            await StreamAsync(client, tls, name, label, visitor, peeked, stoppingToken);
        }
        finally
        {
            Interlocked.Decrement(ref _tunnels);
        }
    }

    private async Task StreamAsync(
        Socket client, bool tls, string name, string label, IPAddress visitor, ReadOnlyMemory<byte> peeked, CancellationToken stoppingToken)
    {
        if (await directory.FindAsync(name, stoppingToken) is { Blocked: true })
        {
            // Blocked by an administrator: never passed on, also not by a tunnel that is still on its way out.
            usage.Refuse(name, label, tls, UsageRefusal.Blocked);
            if (!tls)
            {
                await TryReplyAsync(client, 403, "This address is blocked", "This address is blocked by the operator of MatOS Services.");
            }

            return;
        }

        var session = registry.Get(name);
        if (session is null)
        {
            var known = await directory.FindAsync(name, stoppingToken) is not null;
            if (known)
            {
                usage.Refuse(name, label, tls, UsageRefusal.NoTunnel);
            }

            if (!tls)
            {
                await (known
                    ? TryReplyAsync(client, 502, "This MatOS is offline", "This MatOS is offline. It is not connected to MatOS Services right now; please try again later.")
                    : TryReplyAsync(client, 404, "No MatOS here", "There is no MatOS at this address."));
            }

            return;
        }

        if (!session.TryReserveStream(options.Edge.MaxStreamsPerInstance))
        {
            usage.Refuse(name, label, tls, UsageRefusal.Limit);
            if (!tls)
            {
                await TryReplyAsync(client, 503, "Busy", "This MatOS has too many visitors right now. Please try again in a moment.");
            }

            return;
        }

        PendingStream? pending = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Closed);
            pending = await session.OpenStreamAsync(tls ? 443 : 80, linked.Token);
            var stream = pending is null
                ? null
                : await pending.WaitAsync(TimeSpan.FromSeconds(options.Tunnel.StreamTimeoutSeconds), linked.Token);
            if (stream is null)
            {
                // No open message could be sent (the tunnel went away), or the installation did not pick the stream up in time.
                usage.Refuse(name, label, tls, pending is null || session.Closed.IsCancellationRequested ? UsageRefusal.NoTunnel : UsageRefusal.Timeout);
                if (!tls)
                {
                    await TryReplyAsync(client, 502, "This MatOS did not answer", "This MatOS did not answer in time. Please try again later.");
                }

                return;
            }

            using var meter = usage.Connect(name, label, tls, visitor, session);
            await Pipes.RunAsync(client, stream, peeked, stoppingToken, Pipe(tls, name, meter));
        }
        finally
        {
            if (pending is not null)
            {
                session.Forget(pending);
                pending.Complete();
            }

            session.ReleaseStream();
        }
    }

    private async Task FallbackAsync(Socket client, bool tls, IPEndPoint visitor, ReadOnlyMemory<byte> peeked, CancellationToken stoppingToken)
    {
        var target = EdgeOptions.ParseHostPort(tls ? options.Edge.FallbackHttps : options.Edge.FallbackHttp);
        if (target is null)
        {
            if (!tls)
            {
                await TryReplyAsync(client, 404, "Not found", "There is nothing at this address.");
            }

            return;
        }

        if (Interlocked.Increment(ref _fallbacks) > options.Edge.MaxFallbackConnections)
        {
            Interlocked.Decrement(ref _fallbacks);
            usage.Refuse(UsageRecorder.Fallback, "", tls, UsageRefusal.Limit);
            if (!tls)
            {
                await TryReplyAsync(client, 503, "Busy", "This server is busy. Please try again in a moment.");
            }

            return;
        }

        try
        {
            using var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await upstream.ConnectAsync(target.Value.Host, target.Value.Port, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    logger.LogWarning("The fallback {Host}:{Port} cannot be reached", target.Value.Host, target.Value.Port);
                    if (!tls)
                    {
                        await TryReplyAsync(client, 502, "Bad gateway", "The server behind this address does not answer.");
                    }

                    return;
                }
            }

            if (options.Edge.FallbackProxyProtocol)
            {
                // Before the visitor's first byte, so the existing proxy reads the visitor's address and not the container's.
                await upstream.SendAsync(ProxyProtocol.WriteV2(visitor, (IPEndPoint)client.LocalEndPoint!), SocketFlags.None, stoppingToken);
            }

            using var meter = usage.Connect(UsageRecorder.Fallback, "", tls, visitor.Address);
            await Pipes.RunAsync(client, upstream, peeked, stoppingToken, Pipe(tls, FallbackRoute, meter));
        }
        finally
        {
            Interlocked.Decrement(ref _fallbacks);
        }
    }

    /// <summary>A complete, closing HTTP answer with a short page (the visitor is a person with a browser).</summary>
    private static async Task TryReplyAsync(Socket client, int status, string title, string message)
    {
        var body = $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{title}</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:36rem;margin:4rem auto;padding:0 1rem;color:#222">
            <h1 style="font-size:1.4rem">{title}</h1><p>{message}</p><p style="color:#777;font-size:.9rem">MatOS Services</p></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {ReasonPhrases.GetReasonPhrase(status)}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendAsync(Encoding.ASCII.GetBytes(head), SocketFlags.None, timeout.Token);
            await client.SendAsync(bytes, SocketFlags.None, timeout.Token);
            client.Shutdown(SocketShutdown.Send);

            // Closing with unread input makes the system send a reset, which can throw the answer away at the visitor: read what is
            // still coming (the rest of a request) for a moment first.
            var drain = new byte[4096];
            while (await client.ReceiveAsync(drain, SocketFlags.None, timeout.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
