using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using MatOS.Services.Api;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using MatOS.Services.Instances;
using MatOS.Services.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>DynDNS for everybody: the public page at the zone, the router's updates, the DNS, managing and giving back.</summary>
public partial class DynDnsPageTests
{
    private const string ZoneHost = "my.mattiverse.de";

    [Fact]
    public async Task A_name_registered_on_the_page_follows_the_router_and_can_be_given_back()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();

        // The zone itself serves the page; /dyndns is the same on any host.
        var home = await GetAsync(client, "/", ZoneHost);
        Assert.Contains("Pick a name", home);
        Assert.Contains("Pick a name", await client.GetStringAsync("/dyndns"));
        Assert.DoesNotContain("Pick a name", await client.GetStringAsync("/"));

        var key = await RegisterOnPageAsync(client, "Fritzi");
        Assert.StartsWith("mos_", key);
        using (var scope = service.App.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Instances.SingleAsync(i => i.Name == "fritzi");
            Assert.True(row.DynDnsOnly);
            Assert.Equal(InstanceMode.Direct, row.Mode);
        }

        // No address yet: the name exists, without data.
        var before = await AskAsync(service.Dns, "fritzi.my.mattiverse.de", DnsType.A);
        Assert.Equal((DnsRcode.NoError, 0), (before.Rcode, before.Answers.Count));

        // The FritzBox: its update URL with the placeholders filled, Basic auth with the name and the key.
        using var fritz = service.Client();
        fritz.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"fritzi:{key}")));
        Assert.Equal("good 203.0.113.20 2001:db8::20",
            await fritz.GetStringAsync("/nic/update?hostname=fritzi.my.mattiverse.de&myip=203.0.113.20&myipv6=2001:db8::20"));
        // A FritzBox without IPv6 sends the parameter empty: the stored IPv6 stays.
        Assert.Equal("nochg 203.0.113.20",
            await fritz.GetStringAsync("/nic/update?hostname=fritzi.my.mattiverse.de&myip=203.0.113.20&myipv6="));

        foreach (var name in new[] { "fritzi.my.mattiverse.de", "nas.fritzi.my.mattiverse.de" })
        {
            Assert.Equal(IPAddress.Parse("203.0.113.20"), new IPAddress(Assert.Single((await AskAsync(service.Dns, name, DnsType.A)).Answers).Data));
            Assert.Equal(IPAddress.Parse("2001:db8::20"), new IPAddress(Assert.Single((await AskAsync(service.Dns, name, DnsType.Aaaa)).Answers).Data));
        }

        // A DynDNS name never uses the tunnel.
        using (var tunnel = new ClientWebSocket())
        {
            tunnel.Options.SetRequestHeader("Authorization", "Bearer " + key);
            var refused = await Assert.ThrowsAsync<WebSocketException>(() =>
                tunnel.ConnectAsync(new Uri(new UriBuilder(service.BaseUri) { Scheme = "ws" }.Uri, "/api/v1/tunnel"), CancellationToken.None));
            Assert.Contains("403", refused.Message);
        }

        using var owner = service.Client(key);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/instance", new { mode = "tunnel" })).StatusCode);

        // Manage: sign in, update from this browser (a public address through the trusted proxy), give it back.
        var wrong = await PostFormAsync(client, "/dyndns/manage", new() { ["name"] = "fritzi", ["key"] = "mos_wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var manage = await PostFormAsync(client, "/dyndns/manage", new() { ["name"] = "fritzi", ["key"] = key });
        Assert.Equal(HttpStatusCode.OK, manage.StatusCode);
        var page = await manage.Content.ReadAsStringAsync();
        Assert.Contains("203.0.113.20", page);
        Assert.Contains("2001:db8::20", page);

        var updated = await PostFormAsync(client, "/dyndns/manage/update", new() { ["name"] = "fritzi", ["key"] = key }, "203.0.113.30");
        Assert.Contains("points to 203.0.113.30 now", await updated.Content.ReadAsStringAsync());
        Assert.Equal(IPAddress.Parse("203.0.113.30"), await ResolveAsync(service.Dns, "fritzi.my.mattiverse.de"));

        var local = await PostFormAsync(client, "/dyndns/manage/update", new() { ["name"] = "fritzi", ["key"] = key });
        Assert.Contains("not a public address", await local.Content.ReadAsStringAsync());

        var unconfirmed = await PostFormAsync(client, "/dyndns/manage/release", new() { ["name"] = "fritzi", ["key"] = key });
        Assert.Contains("Tick the box", await unconfirmed.Content.ReadAsStringAsync());
        var released = await PostFormAsync(client, "/dyndns/manage/release", new() { ["name"] = "fritzi", ["key"] = key, ["confirm"] = "yes" });
        Assert.Contains("is free again", await released.Content.ReadAsStringAsync());
        Assert.True((await client.GetFromJsonAsync<NameAvailability>("/api/v1/names/fritzi"))!.Available);
        Assert.Equal(DnsRcode.NxDomain, (await AskAsync(service.Dns, "fritzi.my.mattiverse.de", DnsType.A)).Rcode);
    }

    [Fact]
    public async Task The_page_refuses_other_origins_bots_and_reused_forms()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        var home = await GetAsync(client, "/", ZoneHost);
        var token = TokenRegex().Match(home).Groups[1].Value;
        var bits = int.Parse(BitsRegex().Match(home).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(16, bits);
        var nonce = FormGuard.Solve(token, bits);
        var fields = new Dictionary<string, string> { ["name"] = "robot", ["token"] = token, ["nonce"] = nonce, ["website"] = "" };

        Assert.Equal(HttpStatusCode.Forbidden, (await PostFormAsync(client, "/dyndns/register", fields, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostFormAsync(client, "/dyndns/register", fields, origin: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostFormAsync(client, "/dyndns/register", new(fields) { ["website"] = "http://spam.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostFormAsync(client, "/dyndns/register", new(fields) { ["nonce"] = "1" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await PostFormAsync(client, "/dyndns/register", fields)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostFormAsync(client, "/dyndns/register", new(fields) { ["name"] = "robot2" })).StatusCode);

        // The headers of the page: its own script only.
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/dyndns"));
        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy").Concat(
            response.Content.Headers.TryGetValues("Content-Security-Policy", out var more) ? more : []));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("crypto.subtle", await client.GetStringAsync("/js/dyndns.js"));
    }

    [Fact]
    public async Task A_page_name_obeys_the_limits_and_expires_when_it_never_gets_an_address()
    {
        await using var service = await RunningService.StartAsync("--Services:RegistrationsPerIpPerDay=1", "--Services:ProofOfWorkBits=0");
        using var client = service.Client();
        await RegisterOnPageAsync(client, "idle");
        var home = await GetAsync(client, "/", ZoneHost);
        var second = await PostFormAsync(client, "/dyndns/register",
            new() { ["name"] = "another", ["token"] = TokenRegex().Match(home).Groups[1].Value, ["nonce"] = "", ["website"] = "" });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("Too many new names", await second.Content.ReadAsStringAsync());

        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            await db.Instances.ExecuteUpdateAsync(s => s.SetProperty(i => i.CreateDate, DateTime.UtcNow.AddDays(-8)));
        }

        Assert.Equal(["idle"], await service.App.Services.GetRequiredService<InstanceExpiry>().ExpireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_matos_name_is_managed_in_matos_not_on_the_page()
    {
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("matosname");
        using var client = service.Client();
        var answer = await PostFormAsync(client, "/dyndns/manage", new() { ["name"] = "matosname", ["key"] = key });
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains("Manage it in MatOS", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_blocked_name_can_neither_be_managed_nor_given_back_on_the_page()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        var key = await RegisterOnPageAsync(client, "phish");
        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            var row = await db.Instances.SingleAsync(i => i.Name == "phish");
            await scope.ServiceProvider.GetRequiredService<Admin.InstanceAdmin>().BlockAsync(row, "phishing", "ops@example.org", CancellationToken.None);
        }

        Dictionary<string, string> Fields() => new() { ["name"] = "phish", ["key"] = key, ["confirm"] = "yes" };
        foreach (var path in new[] { "/dyndns/manage", "/dyndns/manage/update", "/dyndns/manage/ports", "/dyndns/manage/release" })
        {
            var answer = await PostFormAsync(client, path, Fields(), "203.0.113.31");
            Assert.True(answer.StatusCode == HttpStatusCode.Forbidden, $"{path}: {answer.StatusCode}");
            Assert.Contains("blocked by the operator", await answer.Content.ReadAsStringAsync());
        }

        // Still there and still blocked: nobody can take the name, and its address did not change.
        var name = await client.GetFromJsonAsync<NameAvailability>("/api/v1/names/phish");
        Assert.Equal((false, "blocked"), (name!.Available, name.Reason));
        using (var scope = service.App.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Instances.SingleAsync(i => i.Name == "phish");
            Assert.Null(row.PublicIpv4);

            // The last line: the registrar never gives a blocked name back, whoever asks.
            var registrar = scope.ServiceProvider.GetRequiredService<Registrar>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registrar.ReleaseAsync(scope.ServiceProvider.GetRequiredService<ServicesDbContext>(), row, CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_ip_endpoint_names_the_caller()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.77");
        Assert.Equal("203.0.113.77", await (await client.SendAsync(request)).Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_form_guard_checks_its_tokens()
    {
        var clock = new ManualClock();
        var guard = new FormGuard(clock);
        var token = guard.Issue();
        Assert.Equal(FormProblem.NoWork, guard.Redeem(token, "0", 30));
        var nonce = FormGuard.Solve(token, 8);
        Assert.Equal(FormProblem.None, guard.Redeem(token, nonce, 8));
        Assert.Equal(FormProblem.Used, guard.Redeem(token, nonce, 8));

        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];
        Assert.Equal(FormProblem.Invalid, guard.Redeem(tampered, null, 0));
        Assert.Equal(FormProblem.Invalid, guard.Redeem("not a token", null, 0));
        Assert.Equal(FormProblem.Invalid, new FormGuard(clock).Redeem(guard.Issue(), null, 0));

        var old = guard.Issue();
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(FormProblem.Expired, guard.Redeem(old, null, 0));

        Assert.Equal(12, FormGuard.LeadingZeroBits([0, 0x08, 0xFF]));
        Assert.Equal(0, FormGuard.LeadingZeroBits([0x80]));
    }

    [Theory]
    [InlineData("https://my.mattiverse.de", null, "my.mattiverse.de", true)]
    [InlineData("https://MY.mattiverse.de", null, "my.mattiverse.de", true)]
    [InlineData(null, "https://my.mattiverse.de/dyndns", "my.mattiverse.de", true)]
    [InlineData("http://127.0.0.1:5000", null, "127.0.0.1:5000", true)]
    [InlineData("https://evil.example", null, "my.mattiverse.de", false)]
    [InlineData("https://my.mattiverse.de.evil.example", null, "my.mattiverse.de", false)]
    [InlineData("null", null, "my.mattiverse.de", false)]
    [InlineData(null, null, "my.mattiverse.de", false)]
    public void The_same_origin_check(string? origin, string? referer, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        Assert.Equal(expected, FormGuard.IsSameOrigin(context.Request));
    }

    /// <summary>What a browser does: load the page, solve the proof of work, post the form; returns the key the page shows.</summary>
    internal static async Task<string> RegisterOnPageAsync(HttpClient client, string name)
    {
        var home = await GetAsync(client, "/", ZoneHost);
        var token = TokenRegex().Match(home).Groups[1].Value;
        var bits = int.Parse(BitsRegex().Match(home).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var response = await PostFormAsync(client, "/dyndns/register",
            new() { ["name"] = name, ["token"] = token, ["nonce"] = FormGuard.Solve(token, bits), ["website"] = "" });
        var page = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, page);
        Assert.Contains("shown only this once", page);
        Assert.Contains("hostname=&lt;domain&gt;&amp;myip=&lt;ipaddr&gt;&amp;myipv6=&lt;ip6addr&gt;", page);
        Assert.Contains("protocol=dyndns2", page);
        return KeyRegex().Match(page).Groups[1].Value;
    }

    internal static async Task<string> GetAsync(HttpClient client, string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>A form post as the page sends it (to the zone host, with its origin); <paramref name="forwardedFor"/> plays the proxy.</summary>
    internal static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields, string? forwardedFor = null, string origin = "https://" + ZoneHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Host = ZoneHost;
        if (origin.Length > 0)
        {
            request.Headers.Add("Origin", origin);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request);
    }

    internal static async Task<DnsAnswer> AskAsync(IPEndPoint dns, string name, ushort type)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(DnsPackets.Query(name, type), dns);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return DnsAnswer.Read((await udp.ReceiveAsync(timeout.Token)).Buffer);
    }

    private static async Task<IPAddress?> ResolveAsync(IPEndPoint dns, string name)
    {
        var answer = await AskAsync(dns, name, DnsType.A);
        return answer.Answers.Count == 0 ? null : new IPAddress(answer.Answers[0].Data);
    }

    [GeneratedRegex("name=\"token\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("data-bits=\"(\\d+)\"")]
    private static partial Regex BitsRegex();

    [GeneratedRegex("<pre id=\"key\"[^>]*>(mos_[A-Za-z0-9_-]+)</pre>")]
    private static partial Regex KeyRegex();
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
