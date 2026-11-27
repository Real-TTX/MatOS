using System.Text.RegularExpressions;
using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Usage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The pages of the statistics: the periods and their buckets, what the database adds up, the charts and the formats.</summary>
public class StatisticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 30, 0, TimeSpan.Zero);

    private static DateTime Utc(int month, int day, int hour = 0, int year = 2026) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_period_has_its_buckets_with_the_current_one_last()
    {
        var day = StatsPeriod.Of("24h", Now);
        Assert.Equal((24, Utc(11, 25, 13), Utc(11, 26, 13), BucketSize.Hour), (day.Buckets.Count, day.From, day.To, day.Size));
        Assert.Equal(23, day.IndexOf(Utc(11, 26, 12)));
        Assert.Equal(0, day.IndexOf(Utc(11, 25, 13)));
        Assert.Equal(-1, day.IndexOf(Utc(11, 25, 12)));
        Assert.Equal(-1, day.IndexOf(Utc(11, 26, 13)));
        Assert.Equal("12:00", day.AxisLabel(Utc(11, 26, 12)));

        var week = StatsPeriod.Of("7d", Now);
        Assert.Equal((7, Utc(11, 20), Utc(11, 27)), (week.Buckets.Count, week.From, week.To));
        Assert.Equal(6, week.IndexOf(Utc(11, 26)));

        var month = StatsPeriod.Of("30d", Now);
        Assert.Equal((30, Utc(10, 28)), (month.Buckets.Count, month.From));

        var year = StatsPeriod.Of("12m", Now);
        Assert.Equal((12, Utc(12, 1, year: 2025), Utc(12, 1)), (year.Buckets.Count, year.From, year.To));
        Assert.Equal(0, year.IndexOf(Utc(12, 15, year: 2025)));
        Assert.Equal(3, year.IndexOf(Utc(3, 10)));
        Assert.Equal(11, year.IndexOf(Utc(11, 26, 12)));
        Assert.Equal("2026-03", year.RowLabel(Utc(3, 1)));

        // Anything else is the last 24 hours.
        Assert.Equal("24h", StatsPeriod.Of("forever", Now).Key);
        Assert.Equal("24h", StatsPeriod.Of(null, Now).Key);
    }

    [Fact]
    public async Task The_database_adds_up_the_hours_and_the_days_per_bucket_installation_app_and_way()
    {
        await using var data = await Database.CreateAsync();
        await data.WriteAsync(db =>
        {
            db.Instances.AddRange(
                new Instance { Name = "anna", KeyHash = "a", CreateDate = Utc(1, 1), CreatedFromIp = "192.0.2.1" },
                // Given back and registered again at 10:30: the earlier holder's 08:00 is not this installation's.
                new Instance { Name = "berta", KeyHash = "b", CreateDate = Utc(11, 26, 10).AddMinutes(30), CreatedFromIp = "192.0.2.2", Mode = InstanceMode.Direct });
            db.UsageHours.AddRange(
                Hour("anna", Utc(11, 26, 11), "", "tls", connections: 2, toInstance: 100, toVisitor: 1000, visitors: 2),
                Hour("anna", Utc(11, 26, 11), "cloud", "tls", connections: 1, toVisitor: 500, visitors: 1, noTunnel: 3),
                Hour("anna", Utc(11, 26, 12), "", "http", connections: 1, toVisitor: 50, visitors: 1),
                Hour("berta", Utc(11, 26, 8), "", "tls", connections: 5, toVisitor: 5000),
                Hour("berta", Utc(11, 26, 10), "", "tls", connections: 1, toVisitor: 10),
                Hour(UsageRecorder.Fallback, Utc(11, 26, 11), "", "tls", connections: 7, toVisitor: 7000),
                // Given back: no installation holds the name now.
                Hour("carla", Utc(11, 26, 11), "", "tls", connections: 2, toVisitor: 20),
                Hour("anna", Utc(11, 20, 5), "", "tls", connections: 1, toVisitor: 1));
            db.UsageDays.Add(new UsageDay { InstanceName = "anna", Day = Utc(3, 10), Host = "", Way = "tls", Connections = 4, BytesToVisitor = 4000 });
            db.DnsHours.AddRange(
                new DnsHour { Name = "anna", Hour = Utc(11, 26, 11), QueriesA = 3, QueriesAaaa = 2, Updates = 1, AddressChanges = 1 },
                new DnsHour { Name = "", Hour = Utc(11, 26, 11), QueriesOther = 1, NxDomain = 4 });
            db.ApiDays.AddRange(new ApiDay { Day = Utc(11, 25), Registrations = 2 }, new ApiDay { Day = Utc(11, 26), NameChecks = 5, RateLimited = 1 });
            for (var i = 0; i < 3; i++)
            {
                db.TunnelSessions.Add(new TunnelSessionEntry { InstanceName = "anna", ConnectedAt = Utc(11, 26, 2 + i), DisconnectedAt = Utc(11, 26, 3 + i) });
            }

            db.TunnelSessions.Add(new TunnelSessionEntry { InstanceName = "berta", ConnectedAt = Utc(11, 26, 9), DisconnectedAt = Utc(11, 26, 10) });

            // The earlier holder of berta dropped three more times before the name was registered again, and so did carla, given back since.
            for (var i = 0; i < 3; i++)
            {
                db.TunnelSessions.Add(new TunnelSessionEntry { InstanceName = "berta", ConnectedAt = Utc(11, 26, 6 + i), DisconnectedAt = Utc(11, 26, 7 + i) });
                db.TunnelSessions.Add(new TunnelSessionEntry { InstanceName = "carla", ConnectedAt = Utc(11, 26, 6 + i), DisconnectedAt = Utc(11, 26, 7 + i) });
            }
        });

        var day = StatsPeriod.Of("24h", Now);
        var year = StatsPeriod.Of("12m", Now);
        await data.ReadAsync(async db =>
        {
            var stats = new UsageStats(db);
            var anna = await db.Instances.SingleAsync(i => i.Name == "anna");
            var berta = await db.Instances.SingleAsync(i => i.Name == "berta");

            // The installations, never the fallback.
            var series = await stats.SeriesAsync(day, null, CancellationToken.None);
            Assert.Equal(24, series.Count);
            Assert.Equal(12, series.Sum(b => b.Connections));
            Assert.Equal((5L, 1620L, 100L, 3L), (series[22].Connections, series[22].Bytes, series[22].BytesToInstance, series[22].RefusedNoTunnel));
            Assert.Equal(1, series[23].Connections);
            Assert.Equal(12, (await stats.SinceAsync(Utc(11, 26), CancellationToken.None)).Connections);

            // A year: the daily rows (rolled up) and the hourly rows in their months.
            var months = await stats.SeriesAsync(year, null, CancellationToken.None);
            Assert.Equal((4L, 4000L), (months[3].Connections, months[3].BytesToVisitor));
            Assert.Equal(13, months[11].Connections);

            // Per installation from its registration on, as its details page counts: the earlier holder of berta is an entry of its own,
            // given back (no link), and so is a name nobody holds now.
            var all = await stats.ByInstanceAsync(day, CancellationToken.None);
            var byInstance = all.Where(u => !u.GivenBack).ToDictionary(u => u.Key, u => u.Totals);
            Assert.Equal(["anna", "berta"], byInstance.Keys.Order());
            Assert.Equal((1650L, 4L, 3L), (byInstance["anna"].Bytes, byInstance["anna"].Visitors, byInstance["anna"].Refused));
            Assert.Equal((1L, 10L), (byInstance["berta"].Connections, byInstance["berta"].Bytes));
            Assert.Equal(
                (await stats.SeriesAsync(day, berta, CancellationToken.None)).Sum(b => b.Bytes),
                byInstance["berta"].Bytes);
            var givenBack = all.Where(u => u.GivenBack).ToDictionary(u => u.Key, u => u.Totals);
            Assert.Equal(["berta", "carla"], givenBack.Keys.Order());
            Assert.Equal((5L, 5000L), (givenBack["berta"].Connections, givenBack["berta"].Bytes));
            Assert.Equal(2, givenBack["carla"].Connections);

            // A period of days: the daily rows are split at the day of the registration.
            var yearByInstance = await stats.ByInstanceAsync(year, CancellationToken.None);
            Assert.Equal(4L + 5, yearByInstance.Single(u => u.Key == "anna" && !u.GivenBack).Totals.Connections);
            Assert.Equal(1, yearByInstance.Single(u => u.Key == "berta" && !u.GivenBack).Totals.Connections);
            Assert.Equal(5, yearByInstance.Single(u => u.Key == "berta" && u.GivenBack).Totals.Connections);

            // One installation: from its registration on.
            Assert.Equal(1, (await stats.SeriesAsync(day, berta, CancellationToken.None)).Sum(b => b.Connections));
            var hosts = (await stats.ByHostAsync(day, anna, CancellationToken.None)).ToDictionary(u => u.Key, u => u.Totals.Bytes);
            Assert.Equal(1150, hosts[""]);
            Assert.Equal(500, hosts["cloud"]);

            var ways = (await stats.ByWayAsync(day, CancellationToken.None)).ToDictionary(u => u.Key, u => u.Totals.Connections);
            Assert.Equal((11L, 1L, 7L), (ways["tunnel tls"], ways["tunnel http"], ways["fallback tls"]));
            Assert.False(ways.ContainsKey("fallback http"));

            var dns = await stats.DnsSeriesAsync(day, null, CancellationToken.None);
            Assert.Equal((10L, 4L, 1L), (dns.Sum(b => b.Queries), dns.Sum(b => b.NxDomain), dns.Sum(b => b.Updates)));
            Assert.Equal(5, (await stats.DnsSeriesAsync(day, anna, CancellationToken.None)).Sum(b => b.Queries));
            var names = await stats.DnsByNameAsync(day, CancellationToken.None);
            Assert.Equal((5L, 5L), (names["anna"].Queries, names[""].Queries));
            Assert.Equal(10, (await stats.DnsSinceAsync(Utc(11, 26), CancellationToken.None)).Queries);

            // The API is counted per day: a period of hours gets the days it touches.
            var api = await stats.ApiSeriesAsync(day, CancellationToken.None);
            Assert.Equal([Utc(11, 25), Utc(11, 26)], api.Select(a => a.Start));
            Assert.Equal((2L, 5L, 1L), (api[0].Totals.Registrations, api[1].Totals.NameChecks, api[1].Totals.RateLimited));
            var week = await stats.ApiSeriesAsync(StatsPeriod.Of("7d", Now), CancellationToken.None);
            Assert.Equal((7, 2L), (week.Count, week[5].Totals.Registrations));

            // ... and says so: today and yesterday, or only today when the 24 hours start at midnight.
            Assert.Equal("today and yesterday", StatsCharts.ApiSpan(day, api.Count));
            Assert.Equal("The API, per day, today and yesterday", StatsCharts.Api("api", day, api).Title);
            var fromMidnight = StatsPeriod.Of("24h", new DateTimeOffset(2026, 11, 26, 23, 10, 0, TimeSpan.Zero));
            var oneDay = await stats.ApiSeriesAsync(fromMidnight, CancellationToken.None);
            Assert.Equal([Utc(11, 26)], oneDay.Select(a => a.Start));
            Assert.Equal("The API, per day, today", StatsCharts.Api("api", fromMidnight, oneDay).Title);
            Assert.Equal("the last 7 days", StatsCharts.ApiSpan(StatsPeriod.Of("7d", Now), week.Count));

            // Dropped often: berta's four sessions all began before the name was registered again (its earlier holder's, no link).
            var dropped = await stats.DroppedOftenAsync(Now.UtcDateTime.AddDays(-1), 3, 8, CancellationToken.None);
            Assert.Equal(
                [("berta", 4, Utc(11, 26, 10), true), ("anna", 3, Utc(11, 26, 5), false), ("carla", 3, Utc(11, 26, 9), true)],
                dropped.Select(d => (d.Name, d.Sessions, d.Last, d.GivenBack)));
            Assert.Equal(10, (await stats.SessionsAsync(null, 10, CancellationToken.None)).Count);
            Assert.Equal(Utc(11, 26, 5), (await stats.SessionsAsync(anna, 10, CancellationToken.None))[0].DisconnectedAt);

            // The earlier holder's session of berta (09:00) is not the new installation's.
            Assert.Empty(await stats.SessionsAsync(berta, 10, CancellationToken.None));
            return true;
        });
    }

    [Fact]
    public void A_chart_is_svg_with_a_title_classes_and_no_style()
    {
        var period = StatsPeriod.Of("24h", Now);
        var a = new long[24];
        var b = new long[24];
        a[23] = 100;
        b[23] = 50;
        a[0] = 30;
        var chart = Chart.Of("traffic", "Traffic <today>", period,
        [
            new("To the visitors", a, AdminFormat.Bytes),
            new("To the installations", b, AdminFormat.Bytes),
            new("Connections", new long[24], AdminFormat.Number, Plot: false),
        ]);

        var svg = chart.Svg().ToString()!;
        // The ids of the drawing never meet those of the page's headings (traffic-title is the heading of the section).
        Assert.Contains("<title id=\"chart-traffic-title\">Traffic &lt;today&gt;</title>", svg);
        Assert.Contains("<desc id=\"chart-traffic-desc\">", svg);
        Assert.Contains("aria-labelledby=\"chart-traffic-title chart-traffic-desc\"", svg);
        Assert.Contains("viewBox=\"0 0 240 100\"", svg);
        Assert.DoesNotContain("style=", svg);
        Assert.DoesNotContain("fill=", svg);
        Assert.Equal(24, Regex.Matches(svg, "msv-chart__hit").Count);

        // Stacked: the highest column reaches the top line (96 of 100), its parts in proportion.
        Assert.Contains("<rect class=\"msv-chart__s1\" x=\"231.5\" y=\"36\" width=\"7\" height=\"64\" />", svg);
        Assert.Contains("<rect class=\"msv-chart__s2\" x=\"231.5\" y=\"4\" width=\"7\" height=\"32\" />", svg);
        Assert.Contains("<rect class=\"msv-chart__s1\" x=\"1.5\" y=\"80.8\" width=\"7\" height=\"19.2\" />", svg);
        Assert.Contains("Highest: 150 B", svg);
        Assert.Contains("2026-11-26 12:00 UTC: To the visitors 100 B, To the installations 50 B", svg);

        // Only the plotted series are in the legend; the table has them all.
        Assert.Equal(2, Regex.Matches(svg, "msv-chart__key--").Count);
        Assert.Equal(3, chart.Series.Count);
        Assert.Contains("The most in 2026-11-26 12:00 UTC", chart.Description());

        var empty = Chart.Of("none", "Nothing", period, [new("Visitors", new long[24], AdminFormat.Number)], ChartKind.Line).Svg().ToString()!;
        Assert.Contains("Nothing was counted in this period.", empty);
        Assert.Contains("msv-chart__line", empty);
        Assert.DoesNotContain("msv-chart__s1", empty);
    }

    [Fact]
    public void Amounts_are_written_in_units_of_1000()
    {
        Assert.Equal("0 B", AdminFormat.Bytes(0));
        Assert.Equal("999 B", AdminFormat.Bytes(999));
        Assert.Equal("1.0 kB", AdminFormat.Bytes(1000));
        Assert.Equal("1.2 MB", AdminFormat.Bytes(1_234_567));
        Assert.Equal("12 MB", AdminFormat.Bytes(12_345_678));
        Assert.Equal("3.4 GB", AdminFormat.Bytes(3_400_000_000));
        Assert.Equal("12,345", AdminFormat.Number(12345));
        Assert.Equal("1.5 h", AdminFormat.Seconds(5400));
    }

    [Fact]
    public void The_stylesheet_of_the_admin_area_uses_tokens_and_classes_of_its_own()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MatOS.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var css = Regex.Replace(File.ReadAllText(Path.Combine(root, "src", "MatOS.Services", "wwwroot", "css", "services.css")), @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(", css);
        Assert.DoesNotMatch(@":\s*(white|black|red|green|blue|gray|grey)\b", css);
        var selectors = Regex.Matches(css, @"(?<![\w-])\.([a-z][a-z0-9_-]*)").Select(m => m.Groups[1].Value).Distinct();
        Assert.All(selectors, c => Assert.True(c.StartsWith("msv-") || c.StartsWith("mat-"), $".{c} is neither of the admin area nor of the toolkit"));
    }

    private static UsageHour Hour(
        string name, DateTime hour, string host, string way, long connections = 0, long toInstance = 0, long toVisitor = 0, long visitors = 0, long noTunnel = 0) =>
        new()
        {
            InstanceName = name, Hour = hour, Host = host, Way = way, Connections = connections, BytesToInstance = toInstance,
            BytesToVisitor = toVisitor, Visitors = visitors, RefusedNoTunnel = noTunnel, PeakStreams = (int)Math.Min(connections, 3),
        };

    /// <summary>A database of the service of its own, migrated.</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly string _path;
        private readonly ServiceProvider _services;

        private Database(string path, ServiceProvider services)
        {
            _path = path;
            _services = services;
        }

        public static async Task<Database> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "matos-services-tests", Guid.NewGuid().ToString("n") + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var services = new ServiceCollection().AddDbContext<ServicesDbContext>(o => o.UseSqlite($"Data Source={path}")).BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Database.MigrateAsync();
            }

            return new Database(path, services);
        }

        public async Task WriteAsync(Action<ServicesDbContext> write)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            write(db);
            await db.SaveChangesAsync();
        }

        public async Task<T> ReadAsync<T>(Func<ServicesDbContext, Task<T>> read)
        {
            await using var scope = _services.CreateAsyncScope();
            return await read(scope.ServiceProvider.GetRequiredService<ServicesDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
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
