using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Services.Tests;

/// <summary>Your own domain through the tunnel: registered, verified by its CNAME, routed by the edge, and only then.</summary>
public class CustomDomainTests
{
    [Fact]
    public async Task A_domain_is_routed_through_the_tunnel_only_after_its_cname_is_found()
    {
        var resolver = new FakeResolver();
        await using var http = LocalServer.Http();
        await using var service = await RunningService.StartWithAsync(s => s.AddSingleton<IDomainResolver>(resolver));
        var key = await service.RegisterAsync("anna");
        await using var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        await service.WaitForTunnelAsync("anna");
        using var owner = service.Client(key);

        // Without the record: pending, and the edge does not know the name.
        var added = await owner.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "Apps.Example.com." });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var pending = (await added.Content.ReadFromJsonAsync<DomainView>())!;
        Assert.Equal(("apps.example.com", "pending"), (pending.Domain, pending.Status));
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(service.Http, "nas.apps.example.com"));

        // The person sets *.apps.example.com CNAME anna.my.mattiverse.de (through one more CNAME, as some providers do).
        resolver.Set("*.apps.example.com", "edge.provider.example", "anna.my.mattiverse.de");
        var again = (await (await owner.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "apps.example.com" }))
            .Content.ReadFromJsonAsync<DomainView>())!;
        Assert.Equal("verified", again.Status);
        Assert.Contains("hello from nas.apps.example.com", await HttpGetAsync(service.Http, "nas.apps.example.com"));
        Assert.Contains("hello from a.b.apps.example.com", await HttpGetAsync(service.Http, "a.b.apps.example.com"));
        Assert.Contains("hello from apps.example.com", await HttpGetAsync(service.Http, "apps.example.com"));
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(service.Http, "example.com"));

        var list = (await owner.GetFromJsonAsync<List<DomainView>>("/api/v1/instance/domains"))!;
        Assert.Equal("apps.example.com", Assert.Single(list).Domain);

        // One domain on its own: what MatOS polls while it waits for the verification (ServicesClient.DomainStatusAsync).
        var one = (await owner.GetFromJsonAsync<DomainView>("/api/v1/instance/domains/Apps.Example.com"))!;
        Assert.Equal(("apps.example.com", "verified"), (one.Domain, one.Status));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/instance/domains/other.example.com")).StatusCode);

        // Another installation cannot take a verified domain.
        var otherKey = await service.RegisterAsync("bert");
        using var other = service.Client(otherKey);
        var conflict = await other.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "apps.example.com" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("domain-taken", (await conflict.Content.ReadFromJsonAsync<ApiError>())!.Error);

        // Removed: no longer routed.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/instance/domains/apps.example.com")).StatusCode);
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(service.Http, "nas.apps.example.com"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync("/api/v1/instance/domains/apps.example.com")).StatusCode);
    }

    [Fact]
    public async Task The_rules_of_a_domain()
    {
        var resolver = new FakeResolver();
        await using var service = await RunningService.StartWithAsync(s => s.AddSingleton<IDomainResolver>(resolver));
        var key = await service.RegisterAsync("carla");
        using var owner = service.Client(key);

        async Task<string?> AddAsync(string domain)
        {
            var response = await owner.PostAsJsonAsync("/api/v1/instance/domains", new { domain });
            return response.IsSuccessStatusCode ? null : (await response.Content.ReadFromJsonAsync<ApiError>())!.Error;
        }

        Assert.Equal("forbidden-domain", await AddAsync("x.my.mattiverse.de"));
        Assert.Equal("forbidden-domain", await AddAsync("mattiverse.de"));
        Assert.Equal("forbidden-domain", await AddAsync("shop.mattiverse.de"));
        Assert.Equal("invalid-domain", await AddAsync("localhost"));
        Assert.Equal("invalid-domain", await AddAsync("203.0.113.1"));
        Assert.Equal("invalid-domain", await AddAsync("-bad.example.com"));

        for (var i = 0; i < CustomDomains.MaxPerInstance; i++)
        {
            Assert.Null(await AddAsync($"d{i}.example.org"));
        }

        Assert.Equal("too-many-domains", await AddAsync("one-more.example.org"));

        // A pending domain does not block its real owner: whoever has the CNAME gets it.
        var otherKey = await service.RegisterAsync("dora");
        using var other = service.Client(otherKey);
        resolver.Set("*.d0.example.org", "dora.my.mattiverse.de");
        var taken = await other.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "d0.example.org" });
        Assert.Equal(HttpStatusCode.Created, taken.StatusCode);
        Assert.Equal("verified", (await taken.Content.ReadFromJsonAsync<DomainView>())!.Status);
        Assert.Equal(CustomDomains.MaxPerInstance - 1, (await owner.GetFromJsonAsync<List<DomainView>>("/api/v1/instance/domains"))!.Count);

        // A DynDNS name has no tunnel, so no domains either.
        using var page = service.Client();
        var dynKey = await DynDnsPageTests.RegisterOnPageAsync(page, "dyno");
        using var dyn = service.Client(dynKey);
        var refused = await dyn.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "dyn.example.net" });
        Assert.Equal("dyndns-only", (await refused.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task A_domain_that_fails_for_three_days_is_removed()
    {
        var resolver = new FakeResolver();
        await using var service = await RunningService.StartWithAsync(s => s.AddSingleton<IDomainResolver>(resolver));
        var key = await service.RegisterAsync("emil");
        using var owner = service.Client(key);
        resolver.Set("*.home.example.com", "emil.my.mattiverse.de");
        resolver.Set("*.fresh.example.com", "emil.my.mattiverse.de");
        await owner.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "home.example.com" });
        await owner.PostAsJsonAsync("/api/v1/instance/domains", new { domain = "fresh.example.com" });

        // The record went away: the next check marks it failed (no longer routed) ...
        resolver.Clear();
        var domains = service.App.Services.GetRequiredService<CustomDomains>();
        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            await db.Domains.ExecuteUpdateAsync(s => s.SetProperty(d => d.CheckedAt, DateTime.UtcNow.AddHours(-7)));
        }

        Assert.Empty(await domains.CheckDueAsync(CancellationToken.None));
        Assert.Null(domains.Find("x.home.example.com"));
        Assert.All((await owner.GetFromJsonAsync<List<DomainView>>("/api/v1/instance/domains"))!, d => Assert.Equal("failed", d.Status));

        // ... and three days of failing remove it; one that recovers stays.
        resolver.Set("*.fresh.example.com", "emil.my.mattiverse.de");
        using (var scope = service.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            await db.Domains.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.CheckedAt, DateTime.UtcNow.AddHours(-7))
                .SetProperty(d => d.FailingSince, DateTime.UtcNow.AddDays(-4)));
        }

        Assert.Equal(["home.example.com"], await domains.CheckDueAsync(CancellationToken.None));
        Assert.Equal("emil", domains.Find("x.fresh.example.com"));
    }

    [Theory]
    [InlineData("apps.example.com", "apps.example.com")]
    [InlineData(" APPS.Example.COM. ", "apps.example.com")]
    [InlineData("example.co.uk", "example.co.uk")]
    [InlineData("my.mattiverse.de", null)]
    [InlineData("a.b.my.mattiverse.de", null)]
    [InlineData("mattiverse.de", null)]
    [InlineData("com", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("under_score.example.com", null)]
    [InlineData("", null)]
    public void Domain_names_are_checked(string input, string? expected)
    {
        var domains = new CustomDomains(new ServicesOptions(), null!, new FakeResolver(), TimeProvider.System, NullLogger<CustomDomains>.Instance);
        Assert.Equal(expected, domains.Normalize(input, out _));
    }

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
}

/// <summary>CNAME records of the test's own; a name below a wildcard (<c>matos-check-…</c>) gets the wildcard's.</summary>
internal sealed class FakeResolver : IDomainResolver
{
    private readonly ConcurrentDictionary<string, string[]> _chains = new(StringComparer.Ordinal);

    public void Set(string name, params string[] chain) => _chains[name] = chain;

    public void Clear() => _chains.Clear();

    public Task<IReadOnlyList<string>> CnameChainAsync(string host, int maxDepth, CancellationToken cancellationToken)
    {
        if (_chains.TryGetValue(host, out var chain) || (host.Contains('.') && _chains.TryGetValue("*" + host[host.IndexOf('.')..], out chain)))
        {
            return Task.FromResult<IReadOnlyList<string>>(chain.Take(maxDepth).ToList());
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
