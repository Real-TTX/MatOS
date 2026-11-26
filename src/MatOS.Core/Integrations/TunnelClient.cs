using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Integrations;

/// <summary>What the settings show of the tunnel.</summary>
/// <param name="Wanted">The free address is used in tunnel mode, so the tunnel should be up.</param>
/// <param name="ConnectedSince">When the control connection came up; null while it is down.</param>
/// <param name="LastError">Why the last attempt failed or the connection ended, in plain words.</param>
/// <param name="StreamsOpen">Visitors whose bytes go through the tunnel right now.</param>
/// <param name="StreamsTotal">Visitors since MatOS started.</param>
public sealed record TunnelState(bool Wanted, DateTime? ConnectedSince, string? LastError, DateTime? LastErrorAt, int StreamsOpen, long StreamsTotal, DateTime? NextAttemptAt)
{
    public bool Connected => ConnectedSince is not null;
}

/// <summary>Where the tunnel takes the visitors: Caddy of Matcad on the shared Docker network.</summary>
public sealed record TunnelEndpoint(string Host, int HttpPort, int HttpsPort)
{
    public int PortFor(int visitorPort) => visitorPort == 443 ? HttpsPort : HttpPort;
}

/// <summary>
/// Finds Caddy, the service of the proxy that takes the routes (<c>routes.service</c> of its catalog entry): its container on the shared network, ports
/// 80 and 443 inside. Outside Docker (local development) the ports the proxy publishes on this host.
/// </summary>
public sealed class TunnelTarget
{
    private readonly MatOSDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;

    public TunnelTarget(MatOSDbContext db, CatalogService catalog, ComposeFiles files, DockerService docker)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
        _docker = docker;
    }

    /// <summary>
    /// Matcad by its catalog id, not by the role: another proxy of the store (Nginx Proxy Manager, role proxy too) must never get the visitors of the free
    /// address (the same rule as <see cref="ProxyPublisher.RoutesAreBindingsAsync"/>).
    /// </summary>
    public static Task<InstalledApp?> MatcadAsync(MatOSDbContext db, CancellationToken ct)
        => db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.CatalogId == Engines.EngineIds.Matcad && a.Lifecycle == AppLifecycle.Installed, ct);

    public async Task<TunnelEndpoint?> FindAsync(CancellationToken ct)
    {
        var proxy = await MatcadAsync(_db, ct);
        if (proxy is null || _catalog.Get(proxy.CatalogId)?.Routes?.Service is not { Length: > 0 } service)
        {
            return null;
        }

        return await _docker.GetSelfContainerNameAsync(ct) is not null
            ? new TunnelEndpoint(_files.ContainerName(proxy.Slug, service), 80, 443)
            : new TunnelEndpoint("localhost", 80, 443);
    }
}

/// <summary>
/// The tunnel to the address service (docs/SERVICES.md "The tunnel"): while the free address is used in tunnel mode, MatOS keeps the control connection
/// (a WebSocket with the key as Bearer), answers every ping with a pong and, for every <c>open</c>, opens the stream WebSocket of that visitor and a TCP
/// connection to Matcad's Caddy and pipes the bytes both ways (<see cref="TunnelPipe"/>). Nothing is decrypted: Caddy holds the certificates. A failed
/// attempt is retried after 1, 2, 5, 10, 30 and then every 60 seconds. <see cref="Refresh"/> makes it look at the settings at once (a new name, another mode).
/// </summary>
public sealed class TunnelClient : BackgroundService
{
    /// <summary>The longest silence on the control connection (the service pings every 30 s) before it counts as dead.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>How often the settings are read again while nothing happens (the wizard may change the domain without telling).</summary>
    private static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(30);

    private const int MaxStreams = 512;
    private const int MaxControlMessage = 64 * 1024;

    private readonly SystemConfigService _config;
    private readonly MatOSOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TunnelClient> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource _changed = new();
    private bool _wanted;
    private DateTime? _connectedSince;
    private string? _lastError;
    private DateTime? _lastErrorAt;
    private DateTime? _nextAttemptAt;
    private int _streamsOpen;
    private long _streamsTotal;
    private TunnelEndpoint? _target;
    private DateTime _targetAt;

    public TunnelClient(SystemConfigService config, IOptions<MatOSOptions> options, IServiceScopeFactory scopes, ILogger<TunnelClient> logger)
    {
        _config = config;
        _options = options.Value;
        _scopes = scopes;
        _logger = logger;
    }

    public TunnelState State
    {
        get
        {
            lock (_gate)
            {
                return new TunnelState(_wanted, _connectedSince, _lastError, _lastErrorAt, Volatile.Read(ref _streamsOpen), Interlocked.Read(ref _streamsTotal), _nextAttemptAt);
            }
        }
    }

    /// <summary>The settings of the free address changed: the tunnel follows at once (it starts, stops or reconnects with the new key).</summary>
    public void Refresh()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new CancellationTokenSource();
        }

        old.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationToken changed;
            lock (_gate)
            {
                changed = _changed.Token;
            }

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
            string? key = null;
            try
            {
                key = await WantedKeyAsync(stoppingToken);
                SetWanted(key is not null);
                if (key is null)
                {
                    failures = 0;
                    await Task.Delay(SettingsInterval, session.Token);
                    continue;
                }

                await RunControlAsync(key, session.Token, stoppingToken, () => failures = 0);
                Fail("The address service closed the connection.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (changed.IsCancellationRequested)
            {
                // The settings changed: look again at once.
                failures = 0;
                SetDisconnected();
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Fail(Describe(ex));
                _logger.LogDebug(ex, "The tunnel to the address service failed.");
            }

            SetDisconnected();
            if (key is null)
            {
                continue;
            }

            var pause = TunnelProtocol.Backoff(failures++);
            lock (_gate)
            {
                _nextAttemptAt = DateTime.UtcNow + pause;
            }

            try
            {
                await Task.Delay(pause, session.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetDisconnected();
    }

    /// <summary>The key when the tunnel should be up: a free address, used as the base domain, in tunnel mode. Null otherwise.</summary>
    private async Task<string?> WantedKeyAsync(CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        return config.Hosted is { IsDirect: false } && config.UsesHostedAddress ? await _config.GetHostedKeyAsync(ct) : null;
    }

    private async Task RunControlAsync(string key, CancellationToken session, CancellationToken stopping, Action connected)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        socket.Options.SetRequestHeader("User-Agent", $"MatOS/{AppVersion.Current}");
        socket.Options.CollectHttpResponseDetails = true;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(session))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await socket.ConnectAsync(TunnelProtocol.ControlUri(_options.ServicesUrl), connect.Token);
            }
            catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TunnelRefusedException("The address service refused the key of this installation. Give the name back and get a new one.");
            }
            catch (OperationCanceledException) when (!session.IsCancellationRequested)
            {
                throw new TimeoutException("The address service did not answer in time.");
            }
        }

        connected();
        lock (_gate)
        {
            _connectedSince = DateTime.UtcNow;
            _nextAttemptAt = null;
        }

        _logger.LogInformation("The tunnel to the address service is up.");

        // The settings may change without a word (the wizard, General): looked at again every half minute while connected. The watcher has a token of its own
        // that ends with this connection however it ends (the caller only disposes the session's source, which would not stop it).
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(session);
        _ = WatchSettingsAsync(key, watch.Token);

        try
        {
            // The streams run on the lifetime of MatOS, not of this connection: a reconnect does not cut the visitors off.
            await ServeAsync(socket, message => StartStream(key, message, stopping), IdleTimeout, session);
        }
        finally
        {
            await watch.CancelAsync();
        }
    }

    private async Task WatchSettingsAsync(string key, CancellationToken session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(SettingsInterval, session);
                if (await WantedKeyAsync(session) != key)
                {
                    Refresh();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the settings for the tunnel failed.");
        }
    }

    /// <summary>
    /// The control loop on a connected WebSocket: answers ping with pong and hands every <c>open</c> to <paramref name="open"/> (which must not block).
    /// Returns when the service closes the connection; throws <see cref="TimeoutException"/> after <paramref name="idle"/> without any message.
    /// </summary>
    public static async Task ServeAsync(WebSocket socket, Action<TunnelMessage> open, TimeSpan idle, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(idle);
        while (true)
        {
            ValueWebSocketReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), silence.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The address service sent nothing for {idle.TotalSeconds:0} seconds.");
            }

            silence.CancelAfter(idle);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                }

                return;
            }

            message.Write(buffer, 0, received.Count);
            if (message.Length > MaxControlMessage)
            {
                throw new InvalidDataException("The address service sent a message that is too large.");
            }

            if (!received.EndOfMessage)
            {
                continue;
            }

            var text = received.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : null;
            message.SetLength(0);
            switch (TunnelProtocol.Parse(text))
            {
                case { Type: TunnelMessage.Ping }:
                    await socket.SendAsync(Encoding.UTF8.GetBytes(TunnelProtocol.PongJson), WebSocketMessageType.Text, endOfMessage: true, ct);
                    break;
                case { Type: TunnelMessage.Open } openMessage:
                    open(openMessage);
                    break;
            }
        }
    }

    private void StartStream(string key, TunnelMessage message, CancellationToken stopping)
    {
        if (Volatile.Read(ref _streamsOpen) >= MaxStreams)
        {
            // The service closes a stream nobody picks up.
            return;
        }

        Interlocked.Increment(ref _streamsOpen);
        Interlocked.Increment(ref _streamsTotal);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunStreamAsync(key, message.Id!, message.Port!.Value, stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "A stream of the tunnel failed.");
            }
            finally
            {
                Interlocked.Decrement(ref _streamsOpen);
            }
        }, CancellationToken.None);
    }

    /// <summary>One visitor: the connection to Caddy, the stream WebSocket of the service, and the bytes between them.</summary>
    private async Task RunStreamAsync(string key, string id, int port, CancellationToken stopping)
    {
        using var connection = new TcpClient { NoDelay = true };
        string? failure = null;
        var target = await FindTargetAsync(stopping);
        if (target is null)
        {
            failure = "Matcad is not installed.";
        }
        else
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await connection.ConnectAsync(target.Host, target.PortFor(port), connect.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !stopping.IsCancellationRequested)
            {
                failure = "Matcad does not answer.";
                ForgetTarget();
            }
        }

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        using (var open = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            open.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(TunnelProtocol.StreamUri(_options.ServicesUrl, id), open.Token);
        }

        if (failure is not null)
        {
            // Tell the service at once instead of letting the visitor wait for its timeout.
            await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, failure, stopping);
            return;
        }

        var stream = connection.GetStream();
        await TunnelPipe.RunAsync(socket, stream, () =>
        {
            try
            {
                connection.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Caddy closed it already.
            }
        }, stopping);
    }

    /// <summary>Caddy's address, asked again once a minute and after a failed connection (Matcad may be reinstalled).</summary>
    private async Task<TunnelEndpoint?> FindTargetAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_target is not null && DateTime.UtcNow - _targetAt < TimeSpan.FromMinutes(1))
            {
                return _target;
            }
        }

        using var scope = _scopes.CreateScope();
        var target = await scope.ServiceProvider.GetRequiredService<TunnelTarget>().FindAsync(ct);
        lock (_gate)
        {
            _target = target;
            _targetAt = DateTime.UtcNow;
        }

        return target;
    }

    private void ForgetTarget()
    {
        lock (_gate)
        {
            _target = null;
        }
    }

    private void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            _wanted = wanted;
            if (!wanted)
            {
                _lastError = null;
                _lastErrorAt = null;
                _nextAttemptAt = null;
            }
        }
    }

    private void SetDisconnected()
    {
        lock (_gate)
        {
            _connectedSince = null;
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _lastError = message;
            _lastErrorAt = DateTime.UtcNow;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TunnelRefusedException or TimeoutException or InvalidDataException => ex.Message,
        WebSocketException or HttpRequestException or SocketException => $"The address service cannot be reached: {ex.Message}",
        _ => $"The tunnel failed: {ex.Message}",
    };

    private sealed class TunnelRefusedException(string message) : Exception(message);
}
