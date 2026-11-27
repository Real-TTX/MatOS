using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MatOS.Services.Api;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The whole way a visitor goes: edge, tunnel, a fake MatOS and a local server behind it; and the API around it.</summary>
public class EndToEndTests
{
    [Fact]
    public async Task Tls_and_http_reach_the_installation_through_the_tunnel()
    {
        await using var http = LocalServer.Http();
        using var certificate = TlsHelpers.CreateCertificate("app.anna.my.mattiverse.de");
        await using var tls = LocalServer.TlsEcho(certificate);
        await using var service = await RunningService.StartAsync($"--Services:Edge:FallbackHttp=127.0.0.1:{http.EndPoint.Port}");

        var key = await service.RegisterAsync("anna");
        await using var tunnel = await FakeTunnelClient.ConnectAsync(
            service.BaseUri, key, port => port == 443 ? tls.EndPoint : http.EndPoint);
        await service.WaitForTunnelAsync("anna");

        // TLS: a real handshake end to end, the edge only read the SNI.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(service.Https);
            await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "app.anna.my.mattiverse.de" });
            Assert.Equal("app.anna.my.mattiverse.de", ssl.RemoteCertificate!.Subject[3..]);
            var message = Encoding.ASCII.GetBytes("ping through the tunnel");
            await ssl.WriteAsync(message);
            var echo = new byte[message.Length];
            await ssl.ReadExactlyAsync(echo);
            Assert.Equal(message, echo);
        }

        // HTTP by Host, for the installation and for a name below it.
        Assert.Contains("hello from app.anna.my.mattiverse.de", await HttpGetAsync(service.Http, "app.anna.my.mattiverse.de"));
        Assert.Contains("hello from anna.my.mattiverse.de", await HttpGetAsync(service.Http, "anna.my.mattiverse.de"));

        // Many visitors at once.
        var many = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => HttpGetAsync(service.Http, $"app{i}.anna.my.mattiverse.de")));
        Assert.All(many, (answer, i) => Assert.True(answer.Contains($"hello from app{i}.anna.my.mattiverse.de"), $"app{i}: {answer}"));

        // A name outside the zone goes to the fallback (the existing proxy) unchanged.
        Assert.Contains("hello from example.org", await HttpGetAsync(service.Http, "example.org"));
    }

    [Fact]
    public async Task An_offline_installation_gets_502_and_an_unknown_name_404()
    {
        await using var service = await RunningService.StartAsync();
        await service.RegisterAsync("bert");

        var offline = await HttpGetAsync(service.Http, "app.bert.my.mattiverse.de");
        Assert.StartsWith("HTTP/1.1 502", offline);
        Assert.Contains("This MatOS is offline", offline);

        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(service.Http, "nobody.my.mattiverse.de"));

        // Over TLS there is nothing to say without a certificate: the connection is closed.
        using var client = new TcpClient();
        await client.ConnectAsync(service.Https);
        await client.GetStream().WriteAsync(TlsHelpers.BuildClientHello("app.bert.my.mattiverse.de"));
        var buffer = new byte[16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await ReadOrResetAsync(client.GetStream(), buffer, timeout.Token));
    }

    [Fact]
    public async Task A_stream_that_is_not_picked_up_ends_with_502()
    {
        await using var service = await RunningService.StartAsync("--Services:Tunnel:StreamTimeoutSeconds=1");
        var key = await service.RegisterAsync("carl");

        // A control connection that never opens its streams.
        using var control = new System.Net.WebSockets.ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + key);
        await control.ConnectAsync(new Uri(new UriBuilder(service.BaseUri) { Scheme = "ws" }.Uri, "/api/v1/tunnel"), CancellationToken.None);
        await service.WaitForTunnelAsync("carl");

        var answer = await HttpGetAsync(service.Http, "carl.my.mattiverse.de");
        Assert.StartsWith("HTTP/1.1 502", answer);
        Assert.Contains("did not answer", answer);
    }

    [Fact]
    public async Task A_new_control_connection_replaces_the_old()
    {
        await using var http = LocalServer.Http();
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("dora");

        await using var first = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        await service.WaitForTunnelAsync("dora");
        var firstSession = service.Registry.Get("dora");

        await using var second = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (service.Registry.Get("dora") == firstSession)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(1, service.Registry.Count);
        Assert.Contains("hello from dora.my.mattiverse.de", await HttpGetAsync(service.Http, "dora.my.mattiverse.de"));
    }

    [Fact]
    public async Task The_api_registers_checks_switches_and_gives_back_a_name()
    {
        await using var service = await RunningService.StartAsync();
        using var anonymous = service.Client();

        var check = await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/Emma");
        Assert.Equal(new NameAvailability("emma", true, null, "emma.my.mattiverse.de"), check);
        Assert.Equal("reserved", (await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/www"))!.Reason);
        Assert.Equal("invalid", (await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/x"))!.Reason);

        var registration = await anonymous.PostAsJsonAsync("/api/v1/instances", new { name = "Emma", version = "1.2.3" });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var created = (await registration.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.Equal(("emma", "my.mattiverse.de", "tunnel"), (created.Name, created.Zone, created.Mode));
        Assert.StartsWith("mos_", created.Key);

        Assert.Equal("taken", (await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/emma"))!.Reason);
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/api/v1/instances", new { name = "emma" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/api/v1/instances", new { name = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/instances", new { name = "-x-" })).StatusCode);

        // The key is stored as a hash only.
        using (var scope = service.App.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Instances.SingleAsync(i => i.Name == "emma");
            Assert.NotEqual(created.Key, row.KeyHash);
            Assert.Equal(64, row.KeyHash.Length);
            Assert.Equal("1.2.3", row.Version);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/instance")).StatusCode);
        using var owner = service.Client(created.Key);
        var view = (await owner.GetFromJsonAsync<InstanceView>("/api/v1/instance"))!;
        Assert.Equal(("emma", "tunnel", false, (string?)null), (view.Name, view.Mode, view.TunnelConnected, view.PublicIp));

        // Direct needs an address first.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/instance", new { mode = "direct" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/instance", new { mode = "carrier-pigeon" })).StatusCode);

        // DynDNS2 as Matddns speaks it.
        using var dyndns = service.Client();
        dyndns.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"emma:{created.Key}")));
        Assert.Equal("good 203.0.113.9", await dyndns.GetStringAsync("/nic/update?hostname=emma.my.mattiverse.de&myip=203.0.113.9"));
        Assert.Equal("nochg 203.0.113.9", await dyndns.GetStringAsync("/nic/update?hostname=emma.my.mattiverse.de&myip=203.0.113.9"));
        Assert.Equal("nohost", await dyndns.GetStringAsync("/nic/update?hostname=other.my.mattiverse.de&myip=203.0.113.9"));

        using var wrong = service.Client();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"emma:{InstanceKeysFake()}")));
        Assert.Equal("badauth", await wrong.GetStringAsync("/nic/update?hostname=emma.my.mattiverse.de&myip=203.0.113.10"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/nic/update?hostname=emma.my.mattiverse.de")).StatusCode);

        // The DNS answers the edge in tunnel mode and the installation's address in direct mode.
        Assert.Equal(RunningService.EdgeIpv4, await ResolveAsync(service.Dns, "app.emma.my.mattiverse.de"));
        var switched = await owner.PutAsJsonAsync("/api/v1/instance", new { mode = "direct" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal("direct", (await switched.Content.ReadFromJsonAsync<InstanceView>())!.Mode);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await ResolveAsync(service.Dns, "app.emma.my.mattiverse.de"));

        // The direct check never probes an address that is not public; the test address is a documentation one, so it only says so.
        var probe = (await (await owner.PostAsync("/api/v1/instance/check", null)).Content.ReadFromJsonAsync<CheckResultView>())!;
        Assert.Equal("203.0.113.9", probe.PublicIp);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/instance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/instance")).StatusCode);
        Assert.True((await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/emma"))!.Available);
        Assert.Null(await ResolveAsync(service.Dns, "emma.my.mattiverse.de"));
    }

    [Fact]
    public async Task The_rate_limit_counts_the_forwarded_address_of_a_trusted_proxy()
    {
        await using var service = await RunningService.StartAsync("--Services:RegistrationsPerIpPerDay=2");
        using var client = service.Client();

        async Task<HttpStatusCode> RegisterFrom(string ip, string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/instances") { Content = JsonContent.Create(new { name }) };
            request.Headers.Add("X-Forwarded-For", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("203.0.113.50", "fritz"));
        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("203.0.113.50", "gina"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterFrom("203.0.113.50", "hans"));
        Assert.Equal(HttpStatusCode.Created, await RegisterFrom("203.0.113.51", "hans"));

        using var scope = service.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        Assert.Equal("203.0.113.50", (await db.Instances.SingleAsync(i => i.Name == "fritz")).CreatedFromIp);
    }

    [Fact]
    public async Task A_forwarded_address_from_an_untrusted_peer_is_ignored()
    {
        // Loopback is not trusted here, so the header is somebody's claim and the connection's address counts.
        await using var service = await RunningService.StartAsync("--Services:TrustedProxies=10.0.0.0/8");
        using var client = service.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/instances") { Content = JsonContent.Create(new { name = "ida" }) };
        request.Headers.Add("X-Forwarded-For", "203.0.113.50");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        using var scope = service.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        Assert.Equal("127.0.0.1", (await db.Instances.SingleAsync(i => i.Name == "ida")).CreatedFromIp);
    }

    [Fact]
    public async Task Without_an_account_there_is_no_admin_area()
    {
        await using var without = await RunningService.StartAsync("--Services:PublicUrl=http://127.0.0.1");
        using var client = without.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/SignIn")).StatusCode);
        Assert.Contains("MatOS Services", await client.GetStringAsync("/"));
        Assert.Equal("ok", await client.GetStringAsync("/healthz"));
    }

    [Fact]
    public async Task The_dns_answers_over_tcp_too()
    {
        await using var service = await RunningService.StartAsync();
        await service.RegisterAsync("kurt");

        using var client = new TcpClient();
        await client.ConnectAsync(service.Dns);
        var stream = client.GetStream();
        foreach (var name in new[] { "kurt.my.mattiverse.de", "nobody.my.mattiverse.de" })
        {
            var query = DnsPackets.Query(name, DnsType.A);
            await stream.WriteAsync(new[] { (byte)(query.Length >> 8), (byte)query.Length });
            await stream.WriteAsync(query);
            var length = new byte[2];
            await stream.ReadExactlyAsync(length);
            var response = new byte[(length[0] << 8) | length[1]];
            await stream.ReadExactlyAsync(response);
            var answer = DnsAnswer.Read(response);
            if (name.StartsWith("kurt", StringComparison.Ordinal))
            {
                Assert.Equal(RunningService.EdgeIpv4, new IPAddress(Assert.Single(answer.Answers).Data));
            }
            else
            {
                Assert.Equal(DnsRcode.NxDomain, answer.Rcode);
            }
        }
    }

    private sealed record CheckResultView(string? PublicIp, bool Http, bool Https, string Detail);

    private static string InstanceKeysFake() => "mos_" + new string('A', 43);

    /// <summary>A plain HTTP/1.1 request to the edge; returns everything until the server closes.</summary>
    private static async Task<string> HttpGetAsync(IPEndPoint edge, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(edge);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private static async Task<int> ReadOrResetAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Asks the service's DNS over UDP for an A record; null for NXDOMAIN or no answer.</summary>
    private static async Task<IPAddress?> ResolveAsync(IPEndPoint dns, string name)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(DnsPackets.Query(name, DnsType.A), dns);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await udp.ReceiveAsync(timeout.Token);
        var answer = DnsAnswer.Read(result.Buffer);
        return answer.Answers.Count == 0 ? null : new IPAddress(answer.Answers[0].Data);
    }
}
