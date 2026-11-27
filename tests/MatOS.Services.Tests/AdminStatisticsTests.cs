using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MatOS.Services.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The pages of the statistics in the running service: a request through the tunnel shows up on them, and the live list of the tunnels.</summary>
public partial class AdminAreaTests
{
    [Fact]
    public async Task A_tunnelled_request_shows_up_on_the_pages_of_the_statistics()
    {
        await using var http = LocalServer.Http();
        await using var service = await StartAsync();
        var key = await service.RegisterAsync("lena");
        var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        await service.WaitForTunnelAsync("lena");

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(service.Http);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: cloud.lena.my.mattiverse.de\r\nConnection: close\r\n\r\n"));
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            Assert.Contains("hello from cloud.lena.my.mattiverse.de", await reader.ReadToEndAsync());
        }

        var recorder = service.App.Services.GetRequiredService<UsageRecorder>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (recorder.OpenConnections > 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await recorder.FlushAsync(CancellationToken.None);

        // The live list: only for a session, and with the tunnel and what it carried so far.
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Browser(service).GetAsync("/admin/api/tunnels")).StatusCode);
        var admin = new Browser(service);
        Assert.True(await admin.SignInAsync(Email, Password));
        using (var live = JsonDocument.Parse(await admin.GetStringAsync("/admin/api/tunnels")))
        {
            var row = Assert.Single(live.RootElement.GetProperty("tunnels").EnumerateArray());
            Assert.Equal("lena", row.GetProperty("name").GetString());
            Assert.Equal("1", row.GetProperty("served").GetString());
            Assert.NotEqual("0 B", row.GetProperty("bytes").GetString());
        }

        var tunnels = await admin.GetStringAsync("/admin/Tunnels");
        Assert.Contains("data-live-tunnels", tunnels);
        Assert.Contains("/js/services.js", tunnels);
        Assert.Contains("?name=lena", tunnels);

        foreach (var period in StatsPeriod.All.Select(p => p.Key))
        {
            foreach (var page in new[] { "/admin", "/admin/Statistics", "/admin/Installations/Details?name=lena" })
            {
                var url = page + (page.Contains('?') ? "&" : "?") + "period=" + period;
                var answer = await admin.GetAsync(url);
                Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{url}: {answer.StatusCode}");
                var html = await answer.Content.ReadAsStringAsync();
                Assert.Contains("<svg class=\"msv-chart__plot\"", html);
                Assert.Contains("aria-current=\"true\"", html);
                Assert.DoesNotContain(" style=", html);
                Assert.Contains("/css/services.css", html);
            }
        }

        // The installation's own page names the app the visitor asked for; the overview and the statistics the installation.
        var details = await admin.GetStringAsync("/admin/Installations/Details?name=lena");
        Assert.Matches(@">\s*cloud\s*<", details);
        Assert.Contains("Highest:", details);
        Assert.Contains("href=\"/admin/Installations/Details?name=lena&amp;period=24h\"", await admin.GetStringAsync("/admin/Statistics"));
        Assert.Contains("Most traffic", await admin.GetStringAsync("/admin"));

        // The tunnel ends: the page lists it among the ended ones, and the installation among its sessions.
        await tunnel.DisposeAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (service.Registry.Get("lena") is not null)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await Task.Delay(200);
        await recorder.FlushAsync(CancellationToken.None);
        using (var live = JsonDocument.Parse(await admin.GetStringAsync("/admin/api/tunnels")))
        {
            Assert.Empty(live.RootElement.GetProperty("tunnels").EnumerateArray());
        }

        Assert.Contains("Closed by MatOS or the network", await admin.GetStringAsync("/admin/Tunnels"));
        Assert.Contains("Closed by MatOS or the network", await admin.GetStringAsync("/admin/Installations/Details?name=lena"));

        // Off the host of the API the live list does not exist either.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/tunnels");
        request.Headers.Host = "lena." + RunningService.Zone;
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.SendAsync(request)).StatusCode);
    }
}
