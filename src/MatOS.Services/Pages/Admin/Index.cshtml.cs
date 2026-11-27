using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin;

/// <summary>
/// The overview: the numbers of now and of today, the traffic of a period, the installations that used the most, the latest registrations and
/// the tunnels that dropped often.
/// </summary>
public sealed class IndexModel(ServicesDbContext db, UsageStats stats, TunnelRegistry registry, EdgeService edge, TimeProvider time) : PageModel
{
    /// <summary>The rows of a list of the top installations.</summary>
    public const int Top = 8;

    /// <summary>How often a tunnel must have ended in the last 24 hours to be listed as dropping often.</summary>
    public const int DroppedAtLeast = 3;

    [BindProperty(SupportsGet = true, Name = "period")]
    public string? PeriodKey { get; set; }

    public int Installations { get; private set; }

    public int DynDnsNames { get; private set; }

    public int Blocked { get; private set; }

    public int Tunnels { get; private set; }

    public int Visitors { get; private set; }

    public int RegisteredThisWeek { get; private set; }

    public UsageTotals Today { get; private set; } = new();

    public DnsTotals DnsToday { get; private set; } = new();

    public StatsPeriod Period { get; private set; } = default!;

    public Chart Traffic { get; private set; } = default!;

    public IReadOnlyList<KeyedUsage> TopByTraffic { get; private set; } = [];

    public IReadOnlyList<KeyedUsage> TopByVisitors { get; private set; } = [];

    public IReadOnlyList<InstallationRow> Latest { get; private set; } = [];

    public IReadOnlyList<DroppedTunnel> Dropped { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var now = time.GetUtcNow();
        var weekAgo = now.UtcDateTime.AddDays(-7);
        Installations = await db.Instances.CountAsync(i => !i.DynDnsOnly, ct);
        DynDnsNames = await db.Instances.CountAsync(i => i.DynDnsOnly, ct);
        RegisteredThisWeek = await db.Instances.CountAsync(i => i.CreateDate > weekAgo, ct);
        Blocked = await db.Instances.CountAsync(i => i.Blocked, ct);
        Tunnels = registry.Count;
        Visitors = edge.Connections;

        var today = UsageRecorder.DayOf(now);
        Today = await stats.SinceAsync(today, ct);
        DnsToday = await stats.DnsSinceAsync(today, ct);

        Period = StatsPeriod.Of(PeriodKey, now);
        Traffic = StatsCharts.Traffic("traffic", Period, await stats.SeriesAsync(Period, null, ct));
        var byInstance = await stats.ByInstanceAsync(Period, ct);
        TopByTraffic = byInstance.Where(u => u.Totals.Bytes > 0).OrderByDescending(u => u.Totals.Bytes).ThenBy(u => u.Key, StringComparer.Ordinal).Take(Top).ToList();
        TopByVisitors = byInstance.Where(u => u.Totals.Visitors > 0).OrderByDescending(u => u.Totals.Visitors).ThenBy(u => u.Key, StringComparer.Ordinal).Take(Top).ToList();

        Latest = InstallationRow.Of(
            await db.Instances.AsNoTracking().OrderByDescending(i => i.CreateDate).Take(Top).ToListAsync(ct), registry);
        Dropped = await stats.DroppedOftenAsync(now.UtcDateTime.AddDays(-1), DroppedAtLeast, Top, ct);
    }
}
