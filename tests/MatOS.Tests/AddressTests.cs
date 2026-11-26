using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatOS.Tests;

/// <summary>
/// The free address of the address service on MatOS's side (docs/SERVICES.md): the client of the service, the protocol of the tunnel (messages, back-off,
/// addresses), the control loop and the pipe between a WebSocket and a connection, all against in-memory fakes.
/// </summary>
public class AddressTests
{
    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData("anna", "anna")]
    [InlineData("  Anna-Lena ", "anna-lena")]
    [InlineData("a1b", "a1b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", "abcdefghijklmnopqrstuvwxyz012345")]
    public void A_valid_name_is_lowered(string input, string expected) => Assert.Equal(expected, ServicesClient.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("-anna")]
    [InlineData("anna-")]
    [InlineData("an_na")]
    [InlineData("an.na")]
    [InlineData("xn--anna")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void An_invalid_name_is_refused(string? input) => Assert.Null(ServicesClient.Normalize(input));

    [Fact]
    public async Task An_invalid_or_reserved_name_is_answered_without_asking_the_service()
    {
        var handler = new StubHandler();
        var client = Services(handler);

        var invalid = await client.CheckNameAsync("a_b");
        var reserved = await client.CheckNameAsync("Admin");

        Assert.True(invalid.Ok);
        Assert.Equal("invalid", invalid.Value!.Reason);
        Assert.False(invalid.Value.Available);
        Assert.Equal("reserved", reserved.Value!.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_free_name_comes_back_with_its_address()
    {
        var handler = new StubHandler().Answer("GET /api/v1/names/anna", HttpStatusCode.OK, """{"name":"anna","available":true,"address":"anna.my.mattiverse.de"}""");

        var result = await Services(handler).CheckNameAsync("Anna");

        Assert.True(result.Ok);
        Assert.True(result.Value!.Available);
        Assert.Equal("anna.my.mattiverse.de", result.Value.Address);
        Assert.Equal("https://services.test/api/v1/names/anna", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_says_so_instead_of_throwing()
    {
        var down = await Services(new StubHandler { Throw = new HttpRequestException("refused") }).CheckNameAsync("anna");
        var proxyPage = await Services(new StubHandler().Answer("GET /api/v1/names/anna", HttpStatusCode.BadGateway, "<html>502</html>")).CheckNameAsync("anna");
        var slow = await Services(new StubHandler { Throw = new TaskCanceledException("timeout") }).RegisterAsync("anna", "1.0");

        foreach (var result in new[] { down.Unreachable, proxyPage.Unreachable, slow.Unreachable })
        {
            Assert.True(result);
        }

        Assert.Equal(ServicesClient.UnreachableMessage, down.Message);
        Assert.Contains("cannot be reached", ServicesClient.UnreachableMessage);
    }

    [Fact]
    public async Task A_registration_sends_the_name_and_returns_the_key_once()
    {
        var handler = new StubHandler().Answer("POST /api/v1/instances", HttpStatusCode.Created,
            """{"name":"anna","zone":"my.mattiverse.de","key":"secret-key","mode":"tunnel"}""");

        var result = await Services(handler).RegisterAsync("anna", "nightly-1");

        Assert.True(result.Ok);
        Assert.Equal("secret-key", result.Value!.Key);
        Assert.Equal("my.mattiverse.de", result.Value.Zone);
        var body = handler.Requests.Single().Body!;
        Assert.Equal("anna", body["name"]!.GetValue<string>());
        Assert.Equal("nightly-1", body["version"]!.GetValue<string>());
        Assert.Null(handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task A_refusal_of_the_service_carries_its_code_and_plain_words()
    {
        var taken = await Services(new StubHandler().Answer("POST /api/v1/instances", HttpStatusCode.Conflict, """{"error":"taken"}""")).RegisterAsync("anna", null);
        var limited = await Services(new StubHandler().Answer("POST /api/v1/instances", HttpStatusCode.TooManyRequests, """{"error":"too-many-registrations"}""")).RegisterAsync("anna", null);
        var noIp = await Services(new StubHandler().Answer("PUT /api/v1/instance", HttpStatusCode.Conflict, """{"error":"no-public-ip"}""")).SetModeAsync("k", "direct");

        Assert.False(taken.Unreachable);
        Assert.Equal("taken", taken.Error);
        Assert.Contains("taken", taken.Message);
        Assert.Equal("too-many-registrations", limited.Error);
        Assert.Equal("no-public-ip", noIp.Error);
        Assert.Contains("public IP", noIp.Message);
    }

    [Fact]
    public async Task The_install_is_asked_with_its_key_and_a_refused_key_is_recognised()
    {
        var handler = new StubHandler()
            .Answer("GET /api/v1/instance", HttpStatusCode.OK,
                """{"name":"anna","zone":"my.mattiverse.de","mode":"tunnel","publicIp":"203.0.113.7","tunnelConnected":true,"lastSeen":"2026-11-26T10:00:00Z"}""")
            .Answer("DELETE /api/v1/instance", HttpStatusCode.Unauthorized, string.Empty);
        var client = Services(handler);

        var state = await client.GetAsync("the-key");
        var release = await client.ReleaseAsync("the-key");

        Assert.True(state.Ok);
        Assert.True(state.Value!.TunnelConnected);
        Assert.Equal("203.0.113.7", state.Value.PublicIp);
        Assert.Equal("Bearer the-key", handler.Requests[0].Authorization);
        Assert.False(release.Ok);
        Assert.True(release.KeyRefused);
        Assert.False(release.Unreachable);
    }

    [Fact]
    public async Task The_check_of_the_direct_way_needs_both_ports()
    {
        var handler = new StubHandler().Answer("POST /api/v1/instance/check", HttpStatusCode.OK, """{"publicIp":"203.0.113.7","http":true,"https":false,"detail":"no TLS"}""");

        var result = await Services(handler).CheckDirectAsync("k");

        Assert.True(result.Ok);
        Assert.True(result.Value!.Http);
        Assert.False(result.Value.Reachable);
    }

    [Fact]
    public void Matddns_calls_the_DynDns_endpoint_of_the_service()
        => Assert.Equal("https://services.test/nic/update?hostname={hostname}&myip={ip}", Services(new StubHandler()).DynDnsUpdateUrl);

    [Fact]
    public async Task The_DynDns_group_of_the_direct_mode_is_marked_and_holds_the_address()
    {
        var handler = new StubHandler()
            .Answer("GET /api/v1/domains", HttpStatusCode.OK, "[]")
            .Answer("POST /api/v1/domains", HttpStatusCode.OK, """{"id":"g1","entries":[{"id":"e1","hostname":"anna.my.mattiverse.de"}]}""");
        var matddns = new MatddnsClient(new StubLocator(), new StubFactory(handler));

        var (groupId, entries) = await matddns.UpsertDynDnsGroupAsync(HostedAddressService.GroupName, "https://services.test/nic/update?hostname={hostname}&myip={ip}",
            "anna", "the-key", ["anna.my.mattiverse.de"]);

        Assert.Equal("g1", groupId);
        Assert.Equal("e1", entries["anna.my.mattiverse.de"]);
        var body = handler.Requests.Single(r => r.Method == "POST").Body!;
        Assert.Equal("DynDns", body["kind"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        Assert.Equal("anna", body["dynDns"]!["username"]!.GetValue<string>());
        Assert.Equal("the-key", body["dynDns"]!["password"]!.GetValue<string>());
        Assert.Equal("anna.my.mattiverse.de", body["entries"]![0]!["hostname"]!.GetValue<string>());
        Assert.Null(body["entries"]![0]!["domain"]);
    }

    // ------------------------------------------------------------------ the free address in the settings

    [Fact]
    public void The_free_address_is_used_while_it_is_the_base_domain()
    {
        var config = new SystemConfig { Hosted = new HostedAddress { Name = "anna" }, BaseDomain = "Anna.my.mattiverse.de" };
        Assert.True(config.UsesHostedAddress);
        Assert.Equal("anna.my.mattiverse.de", config.Hosted.Address);

        config.BaseDomain = "example.com";
        Assert.False(config.UsesHostedAddress);
        Assert.False(new SystemConfig { BaseDomain = "anna.my.mattiverse.de" }.UsesHostedAddress);
    }

    [Fact]
    public void A_name_below_the_zone_is_only_the_own_free_address_and_never_with_a_DNS_account()
    {
        var none = new SystemConfig();
        var hosted = new SystemConfig { Hosted = new HostedAddress { Name = "anna" } };

        Assert.Null(HostedAddressService.CheckDomain(none, "example.com", "cloudflare"));
        Assert.Null(HostedAddressService.CheckDomain(none, null, null));
        Assert.NotNull(HostedAddressService.CheckDomain(none, "anna.my.mattiverse.de", null));
        Assert.NotNull(HostedAddressService.CheckDomain(hosted, "bob.my.mattiverse.de", null));
        Assert.NotNull(HostedAddressService.CheckDomain(hosted, "my.mattiverse.de", null));
        Assert.Null(HostedAddressService.CheckDomain(hosted, "anna.my.mattiverse.de", "none"));
        Assert.Contains("no DNS account", HostedAddressService.CheckDomain(hosted, "anna.my.mattiverse.de", "cloudflare"));
    }

    // ------------------------------------------------------------------ the protocol of the tunnel

    [Fact]
    public void The_control_messages_are_read()
    {
        Assert.Equal(TunnelMessage.Ping, TunnelProtocol.Parse("""{"type":"ping"}""")!.Type);
        var open = TunnelProtocol.Parse("""{"type":"open","id":"0f8fad5b-d9cb-469f-a165-70867728950e","port":443}""")!;
        Assert.Equal(TunnelMessage.Open, open.Type);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", open.Id);
        Assert.Equal(443, open.Port);
        Assert.Equal(80, TunnelProtocol.Parse("""{"type":"open","id":"abc","port":80,"extra":true}""")!.Port);
        Assert.Equal("hello", TunnelProtocol.Parse("""{"type":"hello"}""")!.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ping")]
    [InlineData("[1,2]")]
    [InlineData("""{"kind":"ping"}""")]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":"open","port":443}""")]
    [InlineData("""{"type":"open","id":"abc","port":8080}""")]
    [InlineData("""{"type":"open","id":"abc","port":"443"}""")]
    [InlineData("""{"type":"open","id":"../admin","port":443}""")]
    [InlineData("""{"type":"open","id":"a b","port":80}""")]
    public void What_is_not_a_usable_message_is_ignored(string? text) => Assert.Null(TunnelProtocol.Parse(text));

    [Fact]
    public void The_back_off_is_1_2_5_10_30_and_then_60_seconds()
    {
        var pauses = Enumerable.Range(0, 9).Select(i => (int)TunnelProtocol.Backoff(i).TotalSeconds).ToArray();
        Assert.Equal(new[] { 1, 2, 5, 10, 30, 60, 60, 60, 60 }, pauses);
        Assert.Equal(1, TunnelProtocol.Backoff(-3).TotalSeconds);
    }

    [Fact]
    public async Task The_tunnel_takes_the_visitors_to_Matcad_and_never_to_another_proxy()
    {
        var file = Path.Combine(Path.GetTempPath(), "matos-tunnel-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new MatOSDbContext(
                new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + file).Options,
                new SystemCurrentUserAccessor());
            await db.Database.MigrateAsync();

            // Nginx Proxy Manager (role proxy too) first, so a lookup by the role would find it.
            db.InstalledApps.Add(new InstalledApp { Slug = "npm", Name = "Nginx Proxy Manager", CatalogId = "nginx-proxy-manager", Role = AppRoles.Proxy, Lifecycle = AppLifecycle.Installed, ProjectName = "p1" });
            await db.SaveChangesAsync();
            Assert.Null(await TunnelTarget.MatcadAsync(db, default));

            db.InstalledApps.Add(new InstalledApp { Slug = "matcad", Name = "Matcad", CatalogId = EngineIds.Matcad, Role = AppRoles.Proxy, Lifecycle = AppLifecycle.Installed, ProjectName = "p2" });
            await db.SaveChangesAsync();
            Assert.Equal("matcad", (await TunnelTarget.MatcadAsync(db, default))?.Slug);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public void The_WebSocket_addresses_follow_the_address_of_the_service()
    {
        Assert.Equal("wss://services.mattiverse.de/api/v1/tunnel", TunnelProtocol.ControlUri("https://services.mattiverse.de/").ToString());
        Assert.Equal("ws://localhost:8080/api/v1/tunnel", TunnelProtocol.ControlUri("http://localhost:8080").ToString());
        Assert.Equal("wss://services.mattiverse.de/api/v1/tunnel/stream/abc-1", TunnelProtocol.StreamUri("https://services.mattiverse.de", "abc-1").ToString());
        Assert.Equal(443, new TunnelEndpoint("caddy", 80, 443).PortFor(443));
        Assert.Equal(80, new TunnelEndpoint("caddy", 80, 443).PortFor(80));
    }

    [Fact]
    public async Task The_control_loop_answers_ping_with_pong_and_hands_on_open()
    {
        var (mine, service) = SocketPair();
        var opened = new List<TunnelMessage>();
        var loop = TunnelClient.ServeAsync(mine, opened.Add, TimeSpan.FromSeconds(10), CancellationToken.None);

        await SendTextAsync(service, """{"type":"ping"}""");
        Assert.Equal(TunnelProtocol.PongJson, await ReceiveTextAsync(service));

        await SendTextAsync(service, """{"type":"open","id":"s1","port":443}""");
        await SendTextAsync(service, """{"type":"open","id":"s2","port":25}""");
        await SendTextAsync(service, "not json");
        await SendTextAsync(service, """{"type":"ping"}""");
        Assert.Equal(TunnelProtocol.PongJson, await ReceiveTextAsync(service));

        await service.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("s1", Assert.Single(opened).Id);
        Assert.Equal(WebSocketMessageType.Close, (await service.ReceiveAsync(new byte[16], CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task A_silent_control_connection_counts_as_dead()
    {
        var (mine, _) = SocketPair();
        await Assert.ThrowsAsync<TimeoutException>(() => TunnelClient.ServeAsync(mine, _ => { }, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    // ------------------------------------------------------------------ the pipe

    [Fact]
    public async Task The_pipe_copies_both_ways_and_passes_the_end_of_Caddy_on()
    {
        var (mine, service) = SocketPair();
        var (connection, caddy) = DuplexStream.Pair();
        var shutdowns = 0;
        var pipe = TunnelPipe.RunAsync(mine, connection, () => shutdowns++, CancellationToken.None);

        await service.SendAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("GET / HTTP/1.1\r\n\r\n", await ReadAsync(caddy, 18));

        await caddy.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK"));
        Assert.Equal("HTTP/1.1 200 OK", await ReceiveTextAsync(service));

        // Caddy closes: the visitor's side gets the close frame, answers it, and the pipe ends.
        caddy.CompleteWrites();
        var close = await service.ReceiveAsync(new byte[16], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        await service.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await pipe.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, shutdowns);
        Assert.Equal(WebSocketState.Closed, mine.State);
    }

    [Fact]
    public async Task The_end_of_the_visitor_becomes_a_half_close_towards_Caddy()
    {
        var (mine, service) = SocketPair();
        var (connection, caddy) = DuplexStream.Pair();
        var pipe = TunnelPipe.RunAsync(mine, connection, connection.CompleteWrites, CancellationToken.None);

        await service.SendAsync(Encoding.ASCII.GetBytes("bye"), WebSocketMessageType.Binary, true, CancellationToken.None);
        await service.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        // Caddy reads the last bytes and then the end; it may still answer before it closes too.
        Assert.Equal("bye", await ReadAsync(caddy, 3));
        Assert.Equal(0, await caddy.ReadAsync(new byte[8]));
        await caddy.WriteAsync(Encoding.ASCII.GetBytes("late"));
        Assert.Equal("late", await ReceiveTextAsync(service));
        caddy.CompleteWrites();

        await pipe.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketMessageType.Close, (await service.ReceiveAsync(new byte[16], CancellationToken.None)).MessageType);
    }

    [Fact]
    public async Task A_side_that_goes_away_ends_the_pipe()
    {
        var (mine, service) = SocketPair(out var serviceStream);
        var (connection, _) = DuplexStream.Pair();
        var pipe = TunnelPipe.RunAsync(mine, connection, null, CancellationToken.None);

        service.Abort();
        serviceStream.Dispose();

        await pipe.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(WebSocketState.Open, mine.State);
    }

    [Fact]
    public async Task A_cancelled_pipe_stops_at_once()
    {
        var (mine, _) = SocketPair();
        var (connection, _) = DuplexStream.Pair();
        using var cts = new CancellationTokenSource();
        var pipe = TunnelPipe.RunAsync(mine, connection, null, cts.Token);

        await cts.CancelAsync();
        await pipe.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>Two WebSockets over an in-memory connection: MatOS's end (the client) and the service's end.</summary>
    private static (WebSocket Mine, WebSocket Service) SocketPair() => SocketPair(out _);

    private static (WebSocket Mine, WebSocket Service) SocketPair(out DuplexStream serviceStream)
    {
        var (a, b) = DuplexStream.Pair();
        serviceStream = b;
        return (WebSocket.CreateFromStream(a, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero }),
            WebSocket.CreateFromStream(b, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero }));
    }

    private static Task SendTextAsync(WebSocket socket, string text)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var text = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return text.ToString();
            }
        }
    }

    private static async Task<string> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>One end of an in-memory connection (two pipes): what one end writes the other reads; <see cref="CompleteWrites"/> is its half-close.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly PipeReader _input;
        private readonly PipeWriter _output;
        private readonly Stream _reader;
        private readonly Stream _writer;

        private DuplexStream(PipeReader input, PipeWriter output)
        {
            _input = input;
            _output = output;
            _reader = input.AsStream();
            _writer = output.AsStream();
        }

        public static (DuplexStream, DuplexStream) Pair()
        {
            var one = new Pipe();
            var two = new Pipe();
            return (new DuplexStream(one.Reader, two.Writer), new DuplexStream(two.Reader, one.Writer));
        }

        public void CompleteWrites() => _output.Complete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _writer.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _reader.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _reader.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _writer.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _writer.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Complete();
                _output.Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record Seen(string Method, string Uri, JsonNode? Body, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new();

        public List<Seen> Requests { get; } = new();

        public Exception? Throw { get; init; }

        public StubHandler Answer(string key, HttpStatusCode status, string body)
        {
            _answers[key] = (status, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text),
                request.Headers.Authorization?.ToString()));
            if (Throw is not null)
            {
                throw Throw;
            }

            var (status, body) = _answers.TryGetValue($"{request.Method.Method} {request.RequestUri!.AbsolutePath}", out var answer)
                ? answer
                : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, body.StartsWith('<') ? "text/html" : "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubLocator : EngineLocator
    {
        public StubLocator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://engine.test", "key"));
    }

    private static ServicesClient Services(StubHandler handler)
        => new(new StubFactory(handler), Options.Create(new MatOSOptions { ServicesUrl = "https://services.test/" }));
}
