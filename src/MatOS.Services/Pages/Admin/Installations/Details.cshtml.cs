using System.Text.Json;
using MatOS.Services.Admin;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin.Installations;

/// <summary>
/// One installation: its facts, its live tunnel, its traffic and visitors per app, its tunnel sessions, its DNS answers and DynDNS updates, its
/// domains, what administrators did to it, and the actions.
/// </summary>
public sealed class DetailsModel(
    ServicesDbContext db, TunnelRegistry registry, InstanceAdmin admin, UsageStats stats, ServicesOptions options, TimeProvider time) : PageModel
{
    /// <summary>How many ended tunnels the page lists.</summary>
    public const int SessionRows = 20;

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    /// <summary>The period of its statistics (<c>24h</c>, <c>7d</c>, <c>30d</c>, <c>12m</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "period")]
    public string? PeriodKey { get; set; }

    /// <summary>The reason of a block (the form Block).</summary>
    [BindProperty]
    public string? Reason { get; set; }

    public Instance Instance { get; private set; } = default!;

    public InstallationRow Row { get; private set; } = default!;

    public IReadOnlyList<CustomDomain> Domains { get; private set; } = [];

    public IReadOnlyList<AdminAction> History { get; private set; } = [];

    public PortsResult? PortCheck { get; private set; }

    public string Zone => options.Zone;

    /// <summary>When an unused name is given back by itself (<see cref="InstanceExpiry"/>); null when it is in use, blocked or never expires.</summary>
    public DateTime? ExpiresAt =>
        options.ExpireUnusedAfterDays > 0 && !Instance.Blocked && Instance.FirstTunnel is null && Instance.PublicIpv4 is null && Instance.PublicIpv6 is null
            ? Instance.CreateDate.AddDays(options.ExpireUnusedAfterDays)
            : null;

    public DateTimeOffset Now => time.GetUtcNow();

    public StatsPeriod Period { get; private set; } = default!;

    public UsageTotals Usage { get; private set; } = new();

    public Chart Traffic { get; private set; } = default!;

    public Chart Visitors { get; private set; } = default!;

    public IReadOnlyList<KeyedUsage> ByHost { get; private set; } = [];

    public DnsTotals Dns { get; private set; } = new();

    public Chart DnsChart { get; private set; } = default!;

    public IReadOnlyList<TunnelSessionEntry> Sessions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        Domains = Instance.Domains.OrderBy(d => d.Domain, StringComparer.Ordinal).ToList();
        History = await admin.HistoryAsync(Instance.Name, ct);
        PortCheck = ReadPortCheck(Instance.LastPortCheck);

        // Its statistics, from its registration on (an earlier holder of the name is not this installation: InstanceUsage).
        Period = StatsPeriod.Of(PeriodKey, Now);
        var series = await stats.SeriesAsync(Period, Instance, ct);
        Usage = UsageTotals.Sum(series);
        Traffic = StatsCharts.Traffic("traffic", Period, series);
        Visitors = StatsCharts.Visitors("visitors", Period, series);
        ByHost = (await stats.ByHostAsync(Period, Instance, ct)).Where(u => !u.Totals.IsEmpty)
            .OrderByDescending(u => u.Totals.Bytes).ThenByDescending(u => u.Totals.Connections).ThenBy(u => u.Key, StringComparer.Ordinal).ToList();
        var dns = await stats.DnsSeriesAsync(Period, Instance, ct);
        Dns = DnsTotals.Sum(dns);
        DnsChart = StatsCharts.Dns("dns", Period, dns, zone: false);
        Sessions = await stats.SessionsAsync(Instance, SessionRows, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var closed = await admin.DisconnectAsync(Instance, User.Email(), HttpContext.RequestAborted);
        return Done(closed ? "The tunnel was disconnected. MatOS connects again by itself." : "No tunnel was connected.", !closed);
    }

    public async Task<IActionResult> OnPostTunnelModeAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Instance.DynDnsOnly || Instance.Mode == InstanceMode.Tunnel)
        {
            return Done("This name cannot be switched to tunnel mode.", error: true);
        }

        await admin.TunnelModeAsync(Instance, User.Email(), HttpContext.RequestAborted);
        return Done("Switched back to tunnel mode: the name points at the edge again.");
    }

    public async Task<IActionResult> OnPostBlockAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            return Done("Please give a reason for the block.", error: true);
        }

        await admin.BlockAsync(Instance, Reason, User.Email(), HttpContext.RequestAborted);
        return Done($"{Instance.Name} is blocked.");
    }

    public async Task<IActionResult> OnPostUnblockAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        await admin.UnblockAsync(Instance, User.Email(), HttpContext.RequestAborted);
        return Done($"{Instance.Name} is no longer blocked.");
    }

    public async Task<IActionResult> OnPostGiveBackAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Instance.Blocked)
        {
            // Given back, a blocked name would be free for anybody (the API refuses its owner for the same reason): unblock it first.
            return Done($"{Instance.Name} is blocked: unblock it before giving it back.", error: true);
        }

        var name = Instance.Name;
        await admin.GiveBackAsync(Instance, User.Email(), HttpContext.RequestAborted);
        TempData["StatusMessage"] = $"{name} was given back; the name is free again.";
        return RedirectToPage("/Admin/Installations/Index");
    }

    /// <summary>The last port check in words: null when there is none or it cannot be read.</summary>
    public static PortsResult? ReadPortCheck(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PortsResult>(json, PortsApi.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> LoadAsync()
    {
        var name = NameRules.Normalize(Name);
        var instance = await db.Instances.Include(i => i.Domains).FirstOrDefaultAsync(i => i.Name == name, HttpContext.RequestAborted);
        if (instance is null)
        {
            return false;
        }

        Instance = instance;
        Row = new InstallationRow(instance, registry.Get(instance.Name));
        return true;
    }

    private RedirectToPageResult Done(string message, bool error = false)
    {
        TempData["StatusMessage"] = message;
        TempData["IsError"] = error;
        return RedirectToPage(new { name = Instance.Name });
    }
}
