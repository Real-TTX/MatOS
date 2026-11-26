using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Services.Tests;

/// <summary>"Check my ports": only the caller's own public address, a limit, and the shape MatOS reads.</summary>
public class PortCheckTests
{
    [Fact]
    public async Task The_probe_tells_open_closed_and_the_web_server()
    {
        await using var caddy = CaddyLike();
        await using var other = LocalServer.Http();
        var closed = FreePort();
        var check = new PortCheck(TimeProvider.System, NullLogger<PortCheck>.Instance)
        {
            Target = e => new IPEndPoint(IPAddress.Loopback, e.Port switch
            {
                80 => caddy.EndPoint.Port,
                443 => closed,
                _ => other.EndPoint.Port,
            }),
        };

        var result = await check.CheckAsync(IPAddress.Parse("203.0.113.40"), 8443, CancellationToken.None);
        Assert.Equal("203.0.113.40", result.Ipv4);
        Assert.Null(result.Ipv6);
        Assert.Equal([80, 443, 8443], result.Ports.Select(p => p.Port));
        Assert.Equal(new PortResult(80, "open", null, "caddy"), result.Ports[0]);
        Assert.Equal(new PortResult(443, "closed", null, null), result.Ports[1]);
        Assert.Equal(new PortResult(8443, "open", null, null), result.Ports[2]);

        // 80 asked again as the extra port is not listed twice; an IPv6 caller fills the ipv6 fields.
        var v6 = await check.CheckAsync(IPAddress.Parse("2001:db8::40"), 80, CancellationToken.None);
        Assert.Equal((null, "2001:db8::40"), (v6.Ipv4, v6.Ipv6));
        Assert.Equal([80, 443], v6.Ports.Select(p => p.Port));
        Assert.Equal(new PortResult(80, null, "open", "caddy"), v6.Ports[0]);
    }

    [Fact]
    public async Task The_api_checks_only_the_callers_own_public_address_within_a_limit()
    {
        var probed = new List<IPAddress>();
        var closed = FreePort();
        await using var service = await RunningService.StartWithAsync(services => services.AddSingleton(
            new PortCheck(TimeProvider.System, NullLogger<PortCheck>.Instance)
            {
                Target = e =>
                {
                    lock (probed)
                    {
                        probed.Add(e.Address);
                    }

                    return new IPEndPoint(IPAddress.Loopback, closed);
                },
            }));
        using var client = service.Client();

        // The connection itself (loopback) and a private address are never probed.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckAsync(client, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CheckAsync(client, "10.1.2.3", null)).StatusCode);
        Assert.Empty(probed);

        // An address in the body is not looked at: the caller's own is checked.
        var response = await CheckAsync(client, "203.0.113.40", """{"port":8443,"ip":"198.51.100.1"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("203.0.113.40", json.GetProperty("ipv4").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("ipv6").ValueKind);
        Assert.EndsWith("Z", json.GetProperty("checkedAt").GetString());
        var ports = json.GetProperty("ports").EnumerateArray().ToList();
        Assert.Equal([80, 443, 8443], ports.Select(p => p.GetProperty("port").GetInt32()));
        Assert.All(ports, p =>
        {
            Assert.Equal("closed", p.GetProperty("ipv4").GetString());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("ipv6").ValueKind);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("web").ValueKind);
        });
        Assert.All(probed, a => Assert.Equal(IPAddress.Parse("203.0.113.40"), a));

        Assert.Equal(HttpStatusCode.BadRequest, (await CheckAsync(client, "203.0.113.40", """{"port":70000}""")).StatusCode);

        // Six checks per address in ten minutes; then 429 with the seconds to wait.
        for (var i = 1; i < PortCheckLimits.PerAddress; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, "203.0.113.40", null)).StatusCode);
        }

        var refused = await CheckAsync(client, "203.0.113.40", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("too-many-checks", error.GetProperty("error").GetString());
        Assert.InRange(error.GetProperty("retryAfter").GetInt32(), 1, 600);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, "203.0.113.41", null)).StatusCode);

        // An installation signs in with its key: a limit of its own, and the result is kept.
        var key = await service.RegisterAsync("porty");
        using var owner = service.Client(key);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(owner, "203.0.113.40", null)).StatusCode);
        using (var scope = service.App.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Instances.SingleAsync(i => i.Name == "porty");
            Assert.Contains("\"ipv4\":\"203.0.113.40\"", row.LastPortCheck);
        }

        using var wrong = service.Client("mos_" + new string('A', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckAsync(wrong, "203.0.113.40", null)).StatusCode);
    }

    [Fact]
    public async Task The_page_runs_the_check_too()
    {
        var closed = FreePort();
        await using var service = await RunningService.StartWithAsync(services => services.AddSingleton(
            new PortCheck(TimeProvider.System, NullLogger<PortCheck>.Instance) { Target = _ => new IPEndPoint(IPAddress.Loopback, closed) }));
        using var client = service.Client();
        var answer = await DynDnsPageTests.PostFormAsync(client, "/dyndns/ports", new() { ["port"] = "8443" }, "203.0.113.50");
        var page = await answer.Content.ReadAsStringAsync();
        Assert.Contains("Port 8443", page);
        Assert.Contains("the connection was refused", page);
        Assert.Contains("FritzBox", page);

        var local = await DynDnsPageTests.PostFormAsync(client, "/dyndns/ports", new() { ["port"] = "" });
        Assert.Contains("not a public address", await local.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> CheckAsync(HttpClient client, string? forwardedFor, string? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ports/check");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A server that answers like Caddy does on port 80 (a redirect with <c>Server: Caddy</c>).</summary>
    private static LocalServer CaddyLike() => LocalServer.Raw(
        "HTTP/1.1 308 Permanent Redirect\r\nServer: Caddy\r\nLocation: https://example/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
}
