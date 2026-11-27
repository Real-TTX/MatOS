using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MatOS.Services.Api;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The edge's protection and the limits of the API, against the running service.</summary>
public class EdgeTests
{
    [Fact]
    public async Task Two_requests_for_different_installations_on_one_connection_never_reach_the_first()
    {
        await using var annaServer = LocalServer.KeepAliveHttp("anna says");
        await using var bertServer = LocalServer.KeepAliveHttp("bert says");
        await using var service = await RunningService.StartAsync();
        var annaKey = await service.RegisterAsync("anna");
        var bertKey = await service.RegisterAsync("bert");
        await using var anna = await FakeTunnelClient.ConnectAsync(service.BaseUri, annaKey, _ => annaServer.EndPoint);
        await using var bert = await FakeTunnelClient.ConnectAsync(service.BaseUri, bertKey, _ => bertServer.EndPoint);
        await service.WaitForTunnelAsync("anna");
        await service.WaitForTunnelAsync("bert");

        // What a pooling proxy in front of the edge does: one connection, the requests of several visitors one after another.
        using var client = new TcpClient();
        await client.ConnectAsync(service.Http);
        var stream = client.GetStream();
        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n"u8.ToArray());
        Assert.Equal("anna says anna.my.mattiverse.de", await ReadResponseBodyAsync(stream));
        await stream.WriteAsync("GET /x HTTP/1.1\r\nHost: matnote.anna.my.mattiverse.de\r\n\r\n"u8.ToArray());
        Assert.Equal("anna says matnote.anna.my.mattiverse.de", await ReadResponseBodyAsync(stream));

        await stream.WriteAsync("GET /.well-known/acme-challenge/t HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("", await ReadToEndOrResetAsync(stream, timeout.Token));

        // A new connection takes bert's request to bert.
        using var again = new TcpClient();
        await again.ConnectAsync(service.Http);
        await again.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n"u8.ToArray());
        Assert.Equal("bert says bert.my.mattiverse.de", await ReadResponseBodyAsync(again.GetStream()));
    }

    [Fact]
    public async Task Blocking_a_name_ends_the_visitors_connections_that_are_already_open()
    {
        await using var server = LocalServer.KeepAliveHttp("phish says");
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("phish");
        await using var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => server.EndPoint);
        await service.WaitForTunnelAsync("phish");

        // A visitor on a kept-alive connection, piped before the block.
        using var client = new TcpClient();
        await client.ConnectAsync(service.Http);
        var stream = client.GetStream();
        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: phish.my.mattiverse.de\r\n\r\n"u8.ToArray());
        Assert.Equal("phish says phish.my.mattiverse.de", await ReadResponseBodyAsync(stream));

        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            var row = await db.Instances.SingleAsync(i => i.Name == "phish");
            await scope.ServiceProvider.GetRequiredService<Admin.InstanceAdmin>().BlockAsync(row, "phishing", "ops@example.org", CancellationToken.None);
        }

        // The connection ends with the block (not after the idle time): the next request gets nothing of the blocked site.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await stream.WriteAsync("GET /next HTTP/1.1\r\nHost: phish.my.mattiverse.de\r\n\r\n"u8.ToArray(), timeout.Token);
        }
        catch (IOException)
        {
        }

        Assert.DoesNotContain("phish says", await ReadToEndOrResetAsync(stream, timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task A_quiet_connection_is_closed_after_the_idle_time()
    {
        var first = new TaskCompletionSource<byte[]>();
        await using var silent = LocalServer.Silent(first);
        await using var service = await RunningService.StartAsync(
            $"--Services:Edge:FallbackHttp=127.0.0.1:{silent.EndPoint.Port}", "--Services:Edge:IdleTimeoutSeconds=1");

        using var client = new TcpClient();
        await client.ConnectAsync(service.Http);
        await client.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: example.org\r\n\r\n"u8.ToArray());
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal("", await ReadToEndOrResetAsync(client.GetStream(), timeout.Token));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task One_address_gets_only_its_share_of_the_edge()
    {
        // Loopback is not a trusted proxy here, so the edge counts it as a visitor.
        await using var service = await RunningService.StartAsync(
            "--Services:TrustedProxies=10.0.0.0/8", "--Services:Edge:MaxConnectionsPerAddress=2");
        using var one = new TcpClient();
        using var two = new TcpClient();
        await one.ConnectAsync(service.Https);
        await two.ConnectAsync(service.Https);
        await Task.Delay(200);

        using var three = new TcpClient();
        await three.ConnectAsync(service.Https);
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("", await ReadToEndOrResetAsync(three.GetStream(), timeout.Token));
        // Closed at once, not after the 5 s a ClientHello may take.
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_fallback_gets_the_visitors_address_by_proxy_protocol()
    {
        var first = new TaskCompletionSource<byte[]>();
        await using var silent = LocalServer.Silent(first);
        await using var service = await RunningService.StartAsync(
            $"--Services:Edge:FallbackHttp=127.0.0.1:{silent.EndPoint.Port}", "--Services:Edge:FallbackProxyProtocol=true");

        using var client = new TcpClient();
        await client.ConnectAsync(service.Http);
        const string request = "GET / HTTP/1.1\r\nHost: example.org\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        var bytes = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PeekResult.Found, ProxyProtocol.TryRead(bytes, out var length, out var source));
        Assert.Equal(IPAddress.Loopback, source!.Address);
        Assert.Equal(((IPEndPoint)client.Client.LocalEndPoint!).Port, source.Port);
        Assert.StartsWith(request[..10], Encoding.ASCII.GetString(bytes, length, bytes.Length - length));
    }

    [Fact]
    public async Task Behind_a_proxy_with_proxy_protocol_the_header_is_required()
    {
        await using var http = LocalServer.Http();
        await using var service = await RunningService.StartAsync(
            $"--Services:Edge:FallbackHttp=127.0.0.1:{http.EndPoint.Port}", "--Services:Edge:HttpProxyProtocol=true");

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(service.Http);
            await client.GetStream().WriteAsync(
                "PROXY TCP4 203.0.113.9 198.51.100.7 50000 80\r\nGET / HTTP/1.1\r\nHost: example.org\r\nConnection: close\r\n\r\n"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Contains("hello from example.org", await ReadToEndOrResetAsync(client.GetStream(), timeout.Token));
        }

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(service.Http);
            await client.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: example.org\r\nConnection: close\r\n\r\n"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal("", await ReadToEndOrResetAsync(client.GetStream(), timeout.Token));
        }
    }

    [Fact]
    public async Task A_name_deeper_than_an_app_gets_404()
    {
        await using var service = await RunningService.StartAsync();
        await service.RegisterAsync("anna");
        using var client = new TcpClient();
        await client.ConnectAsync(service.Http);
        await client.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: a1.x.anna.my.mattiverse.de\r\nConnection: close\r\n\r\n"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 404", await ReadToEndOrResetAsync(client.GetStream(), timeout.Token));
    }

    [Fact]
    public async Task Parallel_registrations_from_one_address_keep_to_the_limit()
    {
        await using var service = await RunningService.StartAsync("--Services:RegistrationsPerIpPerDay=5");
        using var client = service.Client();
        var answers = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => client.PostAsJsonAsync("/api/v1/instances", new { name = $"name{i:00}" })));
        Assert.Equal(5, answers.Count(a => a.StatusCode == HttpStatusCode.Created));
        Assert.Equal(15, answers.Count(a => a.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Registrations_have_a_ceiling_for_everybody_and_count_an_ipv6_48()
    {
        await using var service = await RunningService.StartAsync("--Services:RegistrationsPerIpPerDay=2", "--Services:RegistrationsPerHour=4");
        using var client = service.Client();

        async Task<HttpStatusCode> RegisterFrom(string ip, string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/instances") { Content = JsonContent.Create(new { name }) };
            request.Headers.Add("X-Forwarded-For", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        // Two /64s of one /48 are one address for the registrations.
        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("2001:db8:1:1::1", "ipv6one"));
        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("2001:db8:1:2::1", "ipv6two"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterFrom("2001:db8:1:3::1", "ipv6three"));

        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("203.0.113.1", "first"));
        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("203.0.113.2", "second"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterFrom("203.0.113.3", "third"));
    }

    [Fact]
    public async Task A_name_that_was_never_used_expires()
    {
        await using var service = await RunningService.StartAsync("--Services:ExpireUnusedAfterDays=7");
        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            var old = DateTime.UtcNow.AddDays(-8);
            db.Instances.AddRange(
                new Instance { Name = "squatted", KeyHash = new string('a', 64), CreateDate = old, CreatedFromIp = "203.0.113.1" },
                new Instance { Name = "tunnelled", KeyHash = new string('b', 64), CreateDate = old, FirstTunnel = old, CreatedFromIp = "203.0.113.1" },
                new Instance { Name = "direct", KeyHash = new string('c', 64), CreateDate = old, PublicIpv4 = "203.0.113.9", CreatedFromIp = "203.0.113.1" },
                new Instance { Name = "fresh", KeyHash = new string('d', 64), CreateDate = DateTime.UtcNow, CreatedFromIp = "203.0.113.1" });
            await db.SaveChangesAsync();
        }

        var directory = service.App.Services.GetRequiredService<InstanceDirectory>();
        Assert.NotNull(await directory.FindAsync("squatted", CancellationToken.None));

        var expired = await service.App.Services.GetRequiredService<InstanceExpiry>().ExpireAsync(CancellationToken.None);
        Assert.Equal(["squatted"], expired);
        Assert.Null(await directory.FindAsync("squatted", CancellationToken.None));
        Assert.NotNull(await directory.FindAsync("tunnelled", CancellationToken.None));
        using var anonymous = service.Client();
        Assert.True((await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/squatted"))!.Available);
        Assert.False((await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/fresh"))!.Available);
    }

    [Fact]
    public async Task The_first_tunnel_marks_a_name_as_used()
    {
        await using var http = LocalServer.Http();
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("lena");
        await using var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        await service.WaitForTunnelAsync("lena");
        using var scope = service.App.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Instances.SingleAsync(i => i.Name == "lena")).FirstTunnel);
    }

    [Fact]
    public async Task A_reserved_name_shows_its_address_and_times_are_utc()
    {
        await using var service = await RunningService.StartAsync();
        using var anonymous = service.Client();
        Assert.Equal(new NameAvailability("www", false, "reserved", "www.my.mattiverse.de"),
            await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/www"));
        Assert.Equal(new NameAvailability("x", false, "invalid", null), await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/x"));

        var key = await service.RegisterAsync("mona");
        using var owner = service.Client(key);
        var read = await owner.GetStringAsync("/api/v1/instance");
        var written = await (await owner.PutAsJsonAsync("/api/v1/instance", new { mode = "tunnel" })).Content.ReadAsStringAsync();
        Assert.Matches("\"lastSeen\":\"[^\"]+Z\"", read);
        Assert.Matches("\"lastSeen\":\"[^\"]+Z\"", written);
    }

    [Fact]
    public async Task Matddns_reaches_the_dyndns_endpoint_with_the_documented_template()
    {
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("nora");
        using var client = service.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"nora:{key}")));

        // docs/SERVICES.md "MatOS's side": the template Matddns fills per record (one family each; the empty one is dropped).
        const string template = "/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}";
        Assert.Equal("good 203.0.113.9", await client.GetStringAsync(MatddnsUrl(template, "nora.my.mattiverse.de", "203.0.113.9")));
        Assert.Equal("good 2001:db8::9", await client.GetStringAsync(MatddnsUrl(template, "nora.my.mattiverse.de", "2001:db8::9")));
        using var owner = service.Client(key);
        var view = (await owner.GetFromJsonAsync<InstanceView>("/api/v1/instance"))!;
        Assert.Equal(("203.0.113.9", "2001:db8::9"), (view.PublicIpv4, view.PublicIpv6));
    }

    [Fact]
    public async Task Wrong_tunnel_keys_from_one_address_are_cut_off()
    {
        await using var service = await RunningService.StartAsync();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 32; i++)
        {
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("Authorization", "Bearer mos_" + new string('A', 43));
            try
            {
                await socket.ConnectAsync(new Uri(new UriBuilder(service.BaseUri) { Scheme = "ws" }.Uri, "/api/v1/tunnel"), CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }

            codes.Add(socket.HttpStatusCode);
        }

        Assert.All(codes.Take(30), c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.All(codes.Skip(30), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    /// <summary>What Matddns' DynDnsClient does with an update URL: fills the placeholders and drops the empty parameters.</summary>
    private static string MatddnsUrl(string template, string hostname, string ip)
    {
        var v6 = IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetworkV6;
        var url = template.Replace("{hostname}", Uri.EscapeDataString(hostname))
            .Replace("{ipv4}", v6 ? "" : Uri.EscapeDataString(ip))
            .Replace("{ipv6}", v6 ? Uri.EscapeDataString(ip) : "");
        var q = url.IndexOf('?');
        return url[..q] + "?" + string.Join('&', url[(q + 1)..].Split('&').Where(p => !p.EndsWith('=')));
    }

    /// <summary>One answer with a Content-Length; returns its body.</summary>
    private static async Task<string> ReadResponseBodyAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 4 || !buffer.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
        {
            Assert.Equal(1, await stream.ReadAsync(one, timeout.Token));
            buffer.Add(one[0]);
        }

        var head = Encoding.ASCII.GetString(buffer.ToArray());
        Assert.StartsWith("HTTP/1.1 200", head);
        var length = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim());
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, timeout.Token);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<string> ReadToEndOrResetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (IOException)
        {
        }

        return text.ToString();
    }
}
