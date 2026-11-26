using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MatOS.Services.Configuration;
using MatOS.Services.Edge;
using MatOS.Services.Instances;

namespace MatOS.Services.Dns;

/// <summary>
/// The authoritative name server of the zone on UDP and TCP (5353 in the container, 53 outside). Only needed for the direct mode, when
/// the zone is delegated to the service; without delegation nobody asks it and it costs nothing.
/// </summary>
public sealed class DnsService(ServicesOptions options, InstanceDirectory directory, ILogger<DnsService> logger) : BackgroundService
{
    /// <summary>A query is small; anything larger over TCP is not one, and the connection ends.</summary>
    private const int MaxTcpMessage = 4096;

    private const int MaxTcpConnections = 128;

    /// <summary>TCP connections of one address (an IPv4 address, an IPv6 /64): a resolver needs one or two, so one host cannot take all.</summary>
    private const int MaxTcpPerAddress = 8;

    private const int MaxTcpQueries = 100;

    private static readonly TimeSpan TcpIdle = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TcpLifetime = TimeSpan.FromMinutes(2);

    private readonly AddressLimits _tcpAddresses = new(MaxTcpPerAddress, startsPerMinute: 0);

    private DnsResponder _responder = null!;

    private Socket? _udp;
    private Socket? _tcp;
    private int _tcpConnections;

    public IPEndPoint? UdpEndPoint => _udp?.LocalEndPoint as IPEndPoint;

    public IPEndPoint? TcpEndPoint => _tcp?.LocalEndPoint as IPEndPoint;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Dns.Enabled)
        {
            return Task.CompletedTask;
        }

        _responder = new DnsResponder(
            new DnsZone(
                options.Zone, options.Dns.NameServerList(), options.Edge.Ipv4(), options.Edge.Ipv6(), (uint)Math.Max(1, options.Dns.Ttl)),
            directory.FindAsync);
        (_udp, _tcp) = BindPair(options.ListenIp(), options.Dns.Port);
        _tcp.Listen(64);
        logger.LogInformation("DNS for {Zone} listening on {Endpoint} (UDP and TCP)", options.Zone, UdpEndPoint);
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// UDP and TCP on one port number. With a fixed port (5353 in the container) a failure is the operator's to see. With port 0 (the tests) the
    /// system picks a TCP port and UDP takes the same number. That number may be taken for UDP or sit in a range Windows reserves for UDP only
    /// (Hyper-V, WSL: "access denied"); Windows hands out free ports one after the other, so a reserved range can swallow many tries in a row:
    /// a try that fails holds its TCP port until the end, so the next one gets another number, up to 400 of them.
    /// </summary>
    private static (Socket Udp, Socket Tcp) BindPair(IPAddress address, int port)
    {
        if (port != 0)
        {
            var udp = Listeners.Bind(address, port, SocketType.Dgram);
            try
            {
                return (udp, Listeners.Bind(address, port, SocketType.Stream));
            }
            catch
            {
                udp.Dispose();
                throw;
            }
        }

        var held = new List<Socket>();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var tcp = Listeners.Bind(address, 0, SocketType.Stream);
                try
                {
                    return (Listeners.Bind(address, ((IPEndPoint)tcp.LocalEndPoint!).Port, SocketType.Dgram), tcp);
                }
                catch (SocketException) when (attempt < 400)
                {
                    held.Add(tcp);
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            held.ForEach(socket => socket.Dispose());
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _udp is null ? Task.CompletedTask : Task.WhenAll(UdpAsync(_udp, stoppingToken), TcpAcceptAsync(_tcp!, stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _udp?.Dispose();
        _tcp?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    private async Task UdpAsync(Socket socket, CancellationToken stoppingToken)
    {
        var buffer = new byte[4096];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken);
                var answer = await _responder.RespondAsync(buffer.AsMemory(0, received.ReceivedBytes), DnsResponder.UdpLimit, stoppingToken);
                if (answer is not null)
                {
                    await socket.SendToAsync(answer, SocketFlags.None, received.RemoteEndPoint, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // ICMP "port unreachable" of an earlier answer surfaces here on some systems; the next query is unaffected.
                logger.LogDebug(ex, "DNS over UDP");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DNS query failed");
            }
        }
    }

    private async Task TcpAcceptAsync(Socket listener, CancellationToken stoppingToken)
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
            catch (SocketException)
            {
                continue;
            }

            if (Interlocked.Increment(ref _tcpConnections) > MaxTcpConnections)
            {
                Interlocked.Decrement(ref _tcpConnections);
                client.Dispose();
                continue;
            }

            var key = client.RemoteEndPoint is IPEndPoint peer ? Networks.LimitKey(peer.Address) : "";
            if (!_tcpAddresses.TryEnter(key))
            {
                Interlocked.Decrement(ref _tcpConnections);
                client.Dispose();
                continue;
            }

            _ = TcpAsync(client, key, stoppingToken);
        }
    }

    /// <summary>
    /// Queries over TCP carry a two-byte length; several may follow on one connection (RFC 7766). A connection ends after
    /// <see cref="TcpLifetime"/> or <see cref="MaxTcpQueries"/> in any case, so a client that asks just often enough to stay inside the
    /// idle time cannot hold a slot for ever.
    /// </summary>
    private async Task TcpAsync(Socket client, string key, CancellationToken stoppingToken)
    {
        using var _ = client;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lifetime.CancelAfter(TcpLifetime);
            var length = new byte[2];
            var message = new byte[MaxTcpMessage];
            for (var queries = 0; queries < MaxTcpQueries; queries++)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(TcpIdle);
                if (!await ReadExactlyAsync(client, length, idle.Token))
                {
                    return;
                }

                int size = BinaryPrimitives.ReadUInt16BigEndian(length);
                if (size == 0 || size > MaxTcpMessage || !await ReadExactlyAsync(client, message.AsMemory(0, size), idle.Token))
                {
                    return;
                }

                var answer = await _responder.RespondAsync(message.AsMemory(0, size), ushort.MaxValue, idle.Token);
                if (answer is null)
                {
                    return;
                }

                var framed = new byte[answer.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)answer.Length);
                answer.CopyTo(framed, 2);
                await client.SendAsync(framed, SocketFlags.None, idle.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _tcpConnections);
            _tcpAddresses.Leave(key);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
