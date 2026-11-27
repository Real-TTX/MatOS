using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using MatOS.Services.Tunnel;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin;

/// <summary>The overview: the numbers of now and the latest registrations (the charts of the statistics come with them).</summary>
public sealed class IndexModel(ServicesDbContext db, TunnelRegistry registry, EdgeService edge, TimeProvider time) : PageModel
{
    public int Installations { get; private set; }

    public int DynDnsNames { get; private set; }

    public int Tunnels { get; private set; }

    public int Visitors { get; private set; }

    public int RegisteredThisWeek { get; private set; }

    public int Blocked { get; private set; }

    public IReadOnlyList<InstallationRow> Latest { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var weekAgo = time.GetUtcNow().UtcDateTime.AddDays(-7);
        Installations = await db.Instances.CountAsync(i => !i.DynDnsOnly, ct);
        DynDnsNames = await db.Instances.CountAsync(i => i.DynDnsOnly, ct);
        RegisteredThisWeek = await db.Instances.CountAsync(i => i.CreateDate > weekAgo, ct);
        Blocked = await db.Instances.CountAsync(i => i.Blocked, ct);
        Tunnels = registry.Count;
        Visitors = edge.Connections;
        Latest = InstallationRow.Of(
            await db.Instances.AsNoTracking().OrderByDescending(i => i.CreateDate).Take(8).ToListAsync(ct), registry);
    }
}
