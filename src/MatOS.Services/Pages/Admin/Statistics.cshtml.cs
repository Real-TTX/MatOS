using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin;

/// <summary>A row of the table "Tunnel or direct": the installations of a mode and what they used.</summary>
public sealed record ModeUsage(string Mode, int Installations, UsageTotals Edge, DnsTotals Dns);

/// <summary>
/// Who uses the service, what and how, in a period: the traffic over time, by installation, by app (the host below an installation), by way
/// (TLS or HTTP, the tunnel or the fallback, tunnel or direct mode), the refusals by reason, the DNS and the API per day.
/// </summary>
public sealed class StatisticsModel(ServicesDbContext db, UsageStats stats, TimeProvider time) : PageModel
{
    /// <summary>The rows of the tables by installation and by app.</summary>
    public const int Top = 25;

    [BindProperty(SupportsGet = true, Name = "period")]
    public string? PeriodKey { get; set; }

    public StatsPeriod Period { get; private set; } = default!;

    public UsageTotals Totals { get; private set; } = new();

    public Chart Traffic { get; private set; } = default!;

    public Chart Visitors { get; private set; } = default!;

    public Chart Refusals { get; private set; } = default!;

    public Chart Dns { get; private set; } = default!;

    public Chart Api { get; private set; } = default!;

    public IReadOnlyList<KeyedUsage> ByInstance { get; private set; } = [];

    /// <summary>Installations with usage in the period (the table shows the first <see cref="Top"/>).</summary>
    public int InstancesWithUsage { get; private set; }

    public IReadOnlyList<KeyedUsage> ByHost { get; private set; } = [];

    public int HostsWithUsage { get; private set; }

    public IReadOnlyList<KeyedUsage> ByWay { get; private set; } = [];

    public IReadOnlyList<ModeUsage> ByMode { get; private set; } = [];

    public IReadOnlyList<KeyedUsage> MostRefused { get; private set; } = [];

    public DnsTotals DnsTotals { get; private set; } = new();

    /// <summary>The answers for the zone itself (its apex, its name servers) and every unknown name.</summary>
    public DnsTotals DnsZone { get; private set; } = new();

    public ApiTotals ApiTotals { get; private set; } = new();

    /// <summary>The time <see cref="ApiTotals"/> covers: the API is counted per day, so for the last 24 hours it is today and yesterday.</summary>
    public string ApiSpan { get; private set; } = "";

    /// <summary>Whether the API covers other time than the period (the days a period of hours touches).</summary>
    public bool ApiSpanDiffers => ApiSpan != Period.Label;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Period = StatsPeriod.Of(PeriodKey, time.GetUtcNow());

        var series = await stats.SeriesAsync(Period, null, ct);
        Totals = UsageTotals.Sum(series);
        Traffic = StatsCharts.Traffic("traffic", Period, series);
        Visitors = StatsCharts.Visitors("visitors", Period, series);
        Refusals = StatsCharts.Refusals("refusals", Period, series);

        var byInstance = (await stats.ByInstanceAsync(Period, ct)).Where(u => !u.Totals.IsEmpty).ToList();
        InstancesWithUsage = byInstance.Count;
        ByInstance = byInstance.OrderByDescending(u => u.Totals.Bytes).ThenByDescending(u => u.Totals.Connections)
            .ThenBy(u => u.Key, StringComparer.Ordinal).Take(Top).ToList();
        MostRefused = byInstance.Where(u => u.Totals.Refused > 0).OrderByDescending(u => u.Totals.Refused)
            .ThenBy(u => u.Key, StringComparer.Ordinal).Take(10).ToList();

        var byHost = (await stats.ByHostAsync(Period, null, ct)).Where(u => !u.Totals.IsEmpty).ToList();
        HostsWithUsage = byHost.Count;
        ByHost = byHost.OrderByDescending(u => u.Totals.Bytes).ThenByDescending(u => u.Totals.Connections)
            .ThenBy(u => u.Key, StringComparer.Ordinal).Take(Top).ToList();

        string[] ways = ["tunnel tls", "tunnel http", "fallback tls", "fallback http"];
        var byWay = (await stats.ByWayAsync(Period, ct)).ToDictionary(u => u.Key);
        ByWay = ways.Select(w => byWay.TryGetValue(w, out var u) ? u : new KeyedUsage(w, new UsageTotals())).ToList();

        var dnsSeries = await stats.DnsSeriesAsync(Period, null, ct);
        DnsTotals = MatOS.Services.Usage.DnsTotals.Sum(dnsSeries);
        Dns = StatsCharts.Dns("dns", Period, dnsSeries, zone: true);
        var dnsByName = await stats.DnsByNameAsync(Period, ct);
        DnsZone = dnsByName.TryGetValue("", out var zone) ? zone : new DnsTotals();

        // Tunnel or direct: the mode an installation has now (DynDNS names are always direct).
        var modes = await db.Instances.AsNoTracking().Select(i => new { i.Name, i.Mode, i.DynDnsOnly }).ToListAsync(ct);
        var usageByName = byInstance.Where(u => !u.GivenBack).ToDictionary(u => u.Key, u => u.Totals, StringComparer.Ordinal);
        ByMode = modes
            .GroupBy(m => m.DynDnsOnly ? "DynDNS names" : m.Mode == InstanceMode.Direct ? "MatOS, direct" : "MatOS, tunnel")
            .OrderBy(g => g.Key == "MatOS, tunnel" ? 0 : g.Key == "MatOS, direct" ? 1 : 2)
            .Select(g => new ModeUsage(
                g.Key,
                g.Count(),
                UsageTotals.Sum(g.Select(m => usageByName.GetValueOrDefault(m.Name) ?? new UsageTotals())),
                MatOS.Services.Usage.DnsTotals.Sum(g.Select(m => dnsByName.GetValueOrDefault(m.Name) ?? new DnsTotals()))))
            .ToList();

        var api = await stats.ApiSeriesAsync(Period, ct);
        Api = StatsCharts.Api("api", Period, api);
        ApiSpan = StatsCharts.ApiSpan(Period, api.Count);
        ApiTotals = new ApiTotals
        {
            Registrations = api.Sum(a => a.Totals.Registrations),
            GiveBacks = api.Sum(a => a.Totals.GiveBacks),
            PortChecks = api.Sum(a => a.Totals.PortChecks),
            NameChecks = api.Sum(a => a.Totals.NameChecks),
            RateLimited = api.Sum(a => a.Totals.RateLimited),
        };
    }
}
