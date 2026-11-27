using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using MatOS.Services.Instances;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Services.Tests;

/// <summary>The statistics: hour buckets, distinct visitors, an idempotent flush, retention and roll-up, and a request through the edge.</summary>
public class UsageTests
{
    private static readonly IPAddress Visitor1 = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress Visitor2 = IPAddress.Parse("203.0.113.2");

    [Fact]
    public async Task Traffic_and_time_land_in_the_hour_they_happened()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        usage.Clock.Advance(TimeSpan.FromMinutes(10));
        var connection = usage.Recorder.Connect("anna", "app", tls: false, Visitor1);
        connection.ToUpstream(100);
        connection.ToClient(1000);

        // The connection lasts into the next hour: the flush moves it there, and what flows from then on counts there.
        usage.Clock.Advance(TimeSpan.FromMinutes(55));
        await usage.Recorder.FlushAsync(CancellationToken.None);
        connection.ToClient(500);
        usage.Clock.Advance(TimeSpan.FromMinutes(1));
        connection.Dispose();
        await usage.Recorder.FlushAsync(CancellationToken.None);

        var rows = await usage.ReadAsync(db => db.UsageHours.OrderBy(h => h.Hour).ToListAsync());
        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal(new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc), first.Hour);
        Assert.Equal(("anna", "app", "http"), (first.InstanceName, first.Host, first.Way));
        Assert.Equal((1L, 100L, 1000L, 1L, 1), (first.Connections, first.BytesToInstance, first.BytesToVisitor, first.Visitors, first.PeakStreams));
        Assert.Equal(55 * 60, first.ConnectionSeconds);

        var second = rows[1];
        Assert.Equal(new DateTime(2026, 11, 26, 13, 0, 0, DateTimeKind.Utc), second.Hour);
        Assert.Equal((0L, 0L, 500L, 1), (second.Connections, second.BytesToInstance, second.BytesToVisitor, second.PeakStreams));
        Assert.Equal(60, second.ConnectionSeconds);
        Assert.Equal(0, usage.Recorder.OpenConnections);
    }

    [Fact]
    public async Task Visitors_are_counted_once_per_hour_and_a_second_flush_writes_nothing_again()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        usage.Recorder.Connect("anna", "", tls: true, Visitor1).Dispose();
        usage.Recorder.Connect("anna", "", tls: true, Visitor1).Dispose();
        usage.Recorder.Connect("anna", "", tls: true, Visitor1.MapToIPv6()).Dispose();
        using (usage.Recorder.Connect("anna", "", tls: true, Visitor2))
        using (usage.Recorder.Connect("anna", "", tls: true, Visitor2))
        {
            await usage.Recorder.FlushAsync(CancellationToken.None);
        }

        await usage.Recorder.FlushAsync(CancellationToken.None);
        await usage.Recorder.FlushAsync(CancellationToken.None);
        var row = Assert.Single(await usage.ReadAsync(db => db.UsageHours.ToListAsync()));
        Assert.Equal((5L, 2L, 2), (row.Connections, row.Visitors, row.PeakStreams));

        // A new visitor adds one; a known one adds nothing.
        usage.Recorder.Connect("anna", "", tls: true, IPAddress.Parse("203.0.113.3")).Dispose();
        usage.Recorder.Connect("anna", "", tls: true, Visitor1).Dispose();
        await usage.Recorder.FlushAsync(CancellationToken.None);
        row = Assert.Single(await usage.ReadAsync(db => db.UsageHours.ToListAsync()));
        Assert.Equal((7L, 3L), (row.Connections, row.Visitors));

        // The next hour starts counting again (a new salt: the hashes of the last hour mean nothing now).
        usage.Clock.Advance(TimeSpan.FromHours(1));
        usage.Recorder.Connect("anna", "", tls: true, Visitor1).Dispose();
        await usage.Recorder.FlushAsync(CancellationToken.None);
        var next = await usage.ReadAsync(db => db.UsageHours.SingleAsync(h => h.Hour == new DateTime(2026, 11, 26, 13, 0, 0, DateTimeKind.Utc)));
        Assert.Equal((1L, 1L), (next.Connections, next.Visitors));
    }

    [Fact]
    public async Task Refusals_count_by_reason_and_made_up_hosts_are_capped()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        usage.Recorder.Refuse("anna", "", tls: false, UsageRefusal.NoTunnel);
        usage.Recorder.Refuse("anna", "", tls: false, UsageRefusal.NoTunnel);
        usage.Recorder.Refuse("anna", "", tls: false, UsageRefusal.Limit);
        usage.Recorder.Refuse("anna", "", tls: false, UsageRefusal.Timeout);
        usage.Recorder.Refuse("anna", "", tls: false, UsageRefusal.Blocked);
        for (var i = 0; i < UsageRecorder.MaxHostsPerHour + 8; i++)
        {
            usage.Recorder.Refuse("bert", $"app{i}", tls: true, UsageRefusal.NoTunnel);
        }

        await usage.Recorder.FlushAsync(CancellationToken.None);
        var anna = await usage.ReadAsync(db => db.UsageHours.SingleAsync(h => h.InstanceName == "anna"));
        Assert.Equal((0L, 2L, 1L, 1L, 1L), (anna.Connections, anna.RefusedNoTunnel, anna.RefusedLimit, anna.RefusedTimeout, anna.RefusedBlocked));

        var bert = await usage.ReadAsync(db => db.UsageHours.Where(h => h.InstanceName == "bert").ToListAsync());
        Assert.Equal(UsageRecorder.MaxHostsPerHour + 1, bert.Count);
        Assert.Equal(8, bert.Single(h => h.Host == UsageRecorder.OtherHosts).RefusedNoTunnel);
    }

    [Fact]
    public async Task Dns_answers_count_per_name_and_nxdomain_for_the_zone_only()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        var anna = new InstanceAddress("anna", InstanceMode.Tunnel, null, null);
        var blocked = new InstanceAddress("bert", InstanceMode.Tunnel, null, null, Blocked: true);
        var responder = new DnsResponder(
            new DnsZone("my.mattiverse.de", ["ns1.my.mattiverse.de"], IPAddress.Parse("198.51.100.7"), null, 60),
            (name, _) => ValueTask.FromResult(name switch { "anna" => anna, "bert" => blocked, _ => null }),
            usage.Recorder);
        async Task AskAsync(string name, ushort type) =>
            await responder.RespondAsync(DnsPackets.Query(name, type), DnsResponder.UdpLimit, CancellationToken.None);

        await AskAsync("anna.my.mattiverse.de", DnsType.A);
        await AskAsync("app.anna.my.mattiverse.de", DnsType.A);
        await AskAsync("anna.my.mattiverse.de", DnsType.Aaaa);
        await AskAsync("anna.my.mattiverse.de", 15);
        await AskAsync("nobody.my.mattiverse.de", DnsType.A);
        await AskAsync("random1.nobody.my.mattiverse.de", DnsType.A);
        await AskAsync("bert.my.mattiverse.de", DnsType.A);
        await AskAsync("my.mattiverse.de", DnsType.Soa);
        await AskAsync("example.org", DnsType.A);
        usage.Recorder.DynDnsUpdated("anna", changed: true);
        usage.Recorder.DynDnsUpdated("anna", changed: false);
        usage.Recorder.Registered();
        usage.Recorder.NameChecked();
        usage.Recorder.NameChecked();
        usage.Recorder.PortChecked();
        usage.Recorder.GaveBack();
        usage.Recorder.RateLimited();
        await usage.Recorder.FlushAsync(CancellationToken.None);

        var rows = await usage.ReadAsync(db => db.DnsHours.ToListAsync());
        Assert.Equal(new[] { "", "anna" }, rows.Select(r => r.Name).Order());
        var row = rows.Single(r => r.Name == "anna");
        Assert.Equal((2L, 1L, 1L, 0L, 2L, 1L), (row.QueriesA, row.QueriesAaaa, row.QueriesOther, row.NxDomain, row.Updates, row.AddressChanges));
        var zone = rows.Single(r => r.Name == "");
        Assert.Equal((3L, 1L), (zone.NxDomain, zone.QueriesOther));

        var api = await usage.ReadAsync(db => db.ApiDays.SingleAsync());
        Assert.Equal(new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc), api.Day);
        Assert.Equal((1L, 1L, 1L, 2L, 1L), (api.Registrations, api.GiveBacks, api.PortChecks, api.NameChecks, api.RateLimited));
    }

    [Fact]
    public async Task A_tunnel_session_is_written_once_with_its_first_reason()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        using var socket = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        var session = new TunnelSession("anna", socket, usage.Clock) { RemoteAddress = "198.51.100.20", Version = "1.4.0" };
        using (var connection = usage.Recorder.Connect("anna", "app", tls: true, Visitor1, session))
        {
            connection.ToUpstream(10);
            connection.ToClient(20);
        }

        usage.Clock.Advance(TimeSpan.FromMinutes(5));
        session.SetCloseReason(TunnelCloseReason.Silent);
        session.SetCloseReason(TunnelCloseReason.Closed);
        usage.Recorder.TunnelEnded(session);
        usage.Recorder.TunnelEnded(session);
        await usage.Recorder.FlushAsync(CancellationToken.None);

        var row = Assert.Single(await usage.ReadAsync(db => db.TunnelSessions.ToListAsync()));
        Assert.Equal(("anna", TunnelCloseReason.Silent, "198.51.100.20", "1.4.0"), (row.InstanceName, row.Reason, row.RemoteAddress, row.Version));
        Assert.Equal((1L, 10L, 20L), (row.StreamsServed, row.BytesToInstance, row.BytesToVisitor));
        Assert.Equal(TimeSpan.FromMinutes(5), row.DisconnectedAt - row.ConnectedAt);
    }

    [Theory]
    [InlineData("MatOS/1.4.0-20261127", "1.4.0-20261127")]
    [InlineData("MatOS/nightly-12-20261127 (Linux)", "nightly-12-20261127")]
    [InlineData("curl/8.0", null)]
    [InlineData("MatOS/<script>", null)]
    [InlineData("MatOS/", null)]
    public void The_version_comes_from_matos_user_agent_only(string userAgent, string? version) =>
        Assert.Equal(version, TunnelEndpoints.Version(userAgent));

    [Fact]
    public async Task Retention_rolls_old_hours_up_into_days_and_removes_what_is_too_old()
    {
        await using var usage = await UsageDatabase.CreateAsync();
        var today = UsageRecorder.DayOf(usage.Clock.GetUtcNow());
        var old = today.AddDays(-40);
        var recent = today.AddDays(-10);
        await usage.WriteAsync(db =>
        {
            db.UsageHours.AddRange(
                Hour(old.AddHours(10), connections: 3, bytes: 100, visitors: 2, peak: 4),
                Hour(old.AddHours(11), connections: 5, bytes: 50, visitors: 3, peak: 2),
                Hour(recent.AddHours(9), connections: 7, bytes: 1, visitors: 1, peak: 1));
            db.UsageDays.AddRange(
                new UsageDay { Day = today.AddDays(-401), InstanceName = "anna", Way = "tls", Connections = 1 },
                new UsageDay { Day = today.AddDays(-399), InstanceName = "anna", Way = "tls", Connections = 1 });
            db.DnsHours.AddRange(
                new DnsHour { Name = "anna", Hour = old.AddHours(1), QueriesA = 2, Updates = 1 },
                new DnsHour { Name = "anna", Hour = old.AddHours(2), QueriesA = 3, AddressChanges = 1 });
            db.ApiDays.Add(new ApiDay { Day = today.AddDays(-401), Registrations = 1 });
            db.TunnelSessions.AddRange(
                new TunnelSessionEntry { InstanceName = "anna", ConnectedAt = today.AddDays(-201), DisconnectedAt = today.AddDays(-200) },
                new TunnelSessionEntry { InstanceName = "anna", ConnectedAt = today.AddDays(-11), DisconnectedAt = today.AddDays(-10) });
        });

        await usage.Retention.RunAsync(CancellationToken.None);
        await usage.Retention.RunAsync(CancellationToken.None);

        var hours = await usage.ReadAsync(db => db.UsageHours.ToListAsync());
        Assert.Equal(recent.AddHours(9), Assert.Single(hours).Hour);
        var days = await usage.ReadAsync(db => db.UsageDays.OrderBy(d => d.Day).ToListAsync());
        Assert.Equal(new[] { today.AddDays(-399), old }, days.Select(d => d.Day));
        var rolled = days[1];
        Assert.Equal((8L, 150L, 5L, 4), (rolled.Connections, rolled.BytesToVisitor, rolled.Visitors, rolled.PeakStreams));

        Assert.Empty(await usage.ReadAsync(db => db.DnsHours.ToListAsync()));
        var dns = Assert.Single(await usage.ReadAsync(db => db.DnsDays.ToListAsync()));
        Assert.Equal((old, 5L, 1L, 1L), (dns.Day, dns.QueriesA, dns.Updates, dns.AddressChanges));
        Assert.Empty(await usage.ReadAsync(db => db.ApiDays.ToListAsync()));
        Assert.Equal(today.AddDays(-10), Assert.Single(await usage.ReadAsync(db => db.TunnelSessions.ToListAsync())).DisconnectedAt);
    }

    [Fact]
    public async Task A_request_through_the_tunnel_shows_up_in_the_statistics_with_its_bytes()
    {
        await using var http = LocalServer.Http();
        await using var service = await RunningService.StartAsync();
        var key = await service.RegisterAsync("lena");
        var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => http.EndPoint);
        await service.WaitForTunnelAsync("lena");

        var request = "GET / HTTP/1.1\r\nHost: app.lena.my.mattiverse.de\r\nConnection: close\r\n\r\n";
        string answer;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(service.Http);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            answer = await reader.ReadToEndAsync();
        }

        Assert.Contains("hello from app.lena.my.mattiverse.de", answer);
        var recorder = service.App.Services.GetRequiredService<UsageRecorder>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (recorder.OpenConnections > 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        // The tunnel ends: its session is written with what it carried.
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
        using var scope = service.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var row = await db.UsageHours.SingleAsync(h => h.InstanceName == "lena");
        Assert.Equal(("app", "http"), (row.Host, row.Way));
        Assert.Equal((1L, 1L, 1), (row.Connections, row.Visitors, row.PeakStreams));
        Assert.Equal(request.Length, row.BytesToInstance);
        Assert.Equal(Encoding.ASCII.GetByteCount(answer), row.BytesToVisitor);

        var session = await db.TunnelSessions.SingleAsync(s => s.InstanceName == "lena");
        Assert.Equal((TunnelCloseReason.Closed, 1L, (long)request.Length), (session.Reason, session.StreamsServed, session.BytesToInstance));
        Assert.Equal("127.0.0.1", session.RemoteAddress);
    }

    private static UsageHour Hour(DateTime hour, long connections, long bytes, long visitors, int peak) => new()
    {
        Hour = hour, InstanceName = "anna", Host = "", Way = "tls", Connections = connections, BytesToVisitor = bytes, Visitors = visitors,
        PeakStreams = peak,
    };

    /// <summary>A database of its own with the recorder and the retention on a clock the test moves.</summary>
    private sealed class UsageDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly ServiceProvider _services;

        private UsageDatabase(string path, ServiceProvider services)
        {
            _path = path;
            _services = services;
            var options = new ServicesOptions();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            Recorder = new UsageRecorder(scopes, new TunnelRegistry(Clock, NullLogger<TunnelRegistry>.Instance), Clock, NullLogger<UsageRecorder>.Instance);
            Retention = new UsageRetention(options, scopes, Clock, NullLogger<UsageRetention>.Instance);
        }

        public ManualClock Clock { get; } = new();

        public UsageRecorder Recorder { get; }

        public UsageRetention Retention { get; }

        public static async Task<UsageDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "matos-services-tests", Guid.NewGuid().ToString("n") + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var services = new ServiceCollection()
                .AddDbContext<ServicesDbContext>(o => o.UseSqlite($"Data Source={path}"))
                .BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Database.MigrateAsync();
            }

            return new UsageDatabase(path, services);
        }

        public async Task<T> ReadAsync<T>(Func<ServicesDbContext, Task<T>> read)
        {
            await using var scope = _services.CreateAsyncScope();
            return await read(scope.ServiceProvider.GetRequiredService<ServicesDbContext>());
        }

        public async Task WriteAsync(Action<ServicesDbContext> write)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            write(db);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Recorder.Dispose();
            Retention.Dispose();
            await _services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}
