using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MatOS.Services.Api;
using MatOS.Services.Dns;
using MatOS.Services.Edge;
using MatOS.Services.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The real service, started in-process on free loopback ports with a database of its own.</summary>
internal sealed class RunningService : IAsyncDisposable
{
    public const string Zone = "my.mattiverse.de";
    public static readonly IPAddress EdgeIpv4 = IPAddress.Parse("198.51.100.7");

    private readonly string _data;

    private RunningService(WebApplication app, string data)
    {
        App = app;
        _data = data;
        BaseUri = new Uri(app.Urls.First());
        var edge = app.Services.GetRequiredService<EdgeService>();
        Https = edge.HttpsEndPoint!;
        Http = edge.HttpEndPoint!;
        Dns = app.Services.GetRequiredService<DnsService>().UdpEndPoint!;
    }

    public WebApplication App { get; }

    public Uri BaseUri { get; }

    public IPEndPoint Https { get; }

    public IPEndPoint Http { get; }

    public IPEndPoint Dns { get; }

    public TunnelRegistry Registry => App.Services.GetRequiredService<TunnelRegistry>();

    public static Task<RunningService> StartAsync(params string[] extra) => StartWithAsync(null, extra);

    /// <summary>Starts with changes to the services (a resolver or a port check of the test's own).</summary>
    public static async Task<RunningService> StartWithAsync(Action<IServiceCollection>? configure, params string[] extra)
    {
        var data = Path.Combine(Path.GetTempPath(), "matos-services-tests", Guid.NewGuid().ToString("n"));
        string[] args =
        [
            "--urls=http://127.0.0.1:0",
            // Not Development: its settings (an administrator password) must not leak into the tests.
            "--environment=Production",
            $"--Services:DataPath={data}",
            "--Services:ListenAddress=127.0.0.1",
            "--Services:Edge:HttpsPort=0",
            "--Services:Edge:HttpPort=0",
            "--Services:Dns:Port=0",
            $"--Services:Edge:PublicIpv4={EdgeIpv4}",
            "--Logging:LogLevel:Default=Warning",
            .. extra,
        ];
        var app = ServicesHost.Build(args, configure);
        await app.StartAsync();
        return new RunningService(app, data);
    }

    public HttpClient Client(string? key = null)
    {
        var client = new HttpClient { BaseAddress = BaseUri };
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    public async Task<string> RegisterAsync(string name)
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/v1/instances", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        return body!.Key;
    }

    public async Task WaitForTunnelAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Registry.Get(name) is null)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// What MatOS's TunnelClient does, reduced to the protocol: keeps the control connection, answers pings and pipes each opened stream to
/// a local server (the stand-in for Matcad's Caddy).
/// </summary>
internal sealed class FakeTunnelClient : IAsyncDisposable
{
    private readonly ClientWebSocket _control = new();
    private readonly Uri _base;
    private readonly string _key;
    private readonly Func<int, IPEndPoint> _target;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private FakeTunnelClient(Uri baseUri, string key, Func<int, IPEndPoint> target)
    {
        _base = new UriBuilder(baseUri) { Scheme = "ws" }.Uri;
        _key = key;
        _target = target;
    }

    public static async Task<FakeTunnelClient> ConnectAsync(Uri baseUri, string key, Func<int, IPEndPoint> target)
    {
        var client = new FakeTunnelClient(baseUri, key, target);
        client._control.Options.SetRequestHeader("Authorization", "Bearer " + key);
        await client._control.ConnectAsync(new Uri(client._base, "/api/v1/tunnel"), CancellationToken.None);
        client._loop = client.RunAsync();
        return client;
    }

    private async Task RunAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _control.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var message = JsonSerializer.Deserialize<TunnelMessage>(buffer.AsSpan(0, result.Count), TunnelSession.Json)!;
                if (message.Type == "ping")
                {
                    await _control.SendAsync("{\"type\":\"pong\"}"u8.ToArray(), WebSocketMessageType.Text, true, _stop.Token);
                }
                else if (message.Type == "open")
                {
                    _ = OpenAsync(message.Id!, message.Port!.Value);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private async Task OpenAsync(string id, int port)
    {
        using var stream = new ClientWebSocket();
        stream.Options.SetRequestHeader("Authorization", "Bearer " + _key);
        await stream.ConnectAsync(new Uri(_base, $"/api/v1/tunnel/stream/{id}"), _stop.Token);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(_target(port), _stop.Token);
        await Pipes.RunAsync(socket, stream, ReadOnlyMemory<byte>.Empty, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _control.Abort();
        if (_loop is not null)
        {
            await _loop;
        }

        _control.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Small local servers behind the tunnel: an HTTP server that names the host it was asked for, and a TLS echo server.</summary>
internal sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private LocalServer(Func<TcpClient, Task> handle)
    {
        _listener.Start();
        _loop = AcceptAsync(handle);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Answers every request with <c>hello from {Host}</c> and closes.</summary>
    public static LocalServer Http(string greeting = "hello from") => new(async client =>
    {
        var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                return;
            }

            length += read;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var host = head.Split("\r\n").First(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
        var body = Encoding.UTF8.GetBytes($"{greeting} {host}");
        var answer = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
        await stream.WriteAsync(body);
        client.Client.Shutdown(SocketShutdown.Send);
    });

    /// <summary>Answers every request on a connection with <c>{greeting} {Host}</c> and keeps the connection open (HTTP/1.1 keep-alive).</summary>
    public static LocalServer KeepAliveHttp(string greeting) => new(async client =>
    {
        var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (true)
        {
            int end;
            while ((end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length));
                if (read == 0)
                {
                    return;
                }

                length += read;
            }

            var head = Encoding.ASCII.GetString(buffer, 0, end);
            var host = head.Split("\r\n").First(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
            var body = Encoding.UTF8.GetBytes($"{greeting} {host}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"));
            await stream.WriteAsync(body);
            buffer.AsSpan(end + 4, length - end - 4).CopyTo(buffer);
            length -= end + 4;
        }
    });

    /// <summary>Answers every request head with <paramref name="answer"/> as it is, and closes.</summary>
    public static LocalServer Raw(string answer) => new(async client =>
    {
        var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                return;
            }

            length += read;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
        client.Client.Shutdown(SocketShutdown.Send);
    });

    /// <summary>Keeps every connection open, says nothing, and hands over the first bytes of the first connection.</summary>
    public static LocalServer Silent(TaskCompletionSource<byte[]> first) => new(async client =>
    {
        var buffer = new byte[4096];
        var read = await client.GetStream().ReadAsync(buffer);
        first.TrySetResult(buffer[..read]);
        while (await client.GetStream().ReadAsync(buffer) > 0)
        {
        }
    });

    /// <summary>Completes a real TLS handshake with <paramref name="certificate"/> and echoes what it receives.</summary>
    public static LocalServer TlsEcho(X509Certificate2 certificate) => new(async client =>
    {
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
        var buffer = new byte[4096];
        int read;
        while ((read = await tls.ReadAsync(buffer)) > 0)
        {
            await tls.WriteAsync(buffer.AsMemory(0, read));
        }
    });

    private async Task AcceptAsync(Func<TcpClient, Task> handle)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await handle(client);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException)
                    {
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
