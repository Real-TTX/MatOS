using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Tunnel;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin.Installations;

/// <summary>Every name below the zone: the list with the toolbar of every Mat app (search, filters, sort, pages).</summary>
public sealed class IndexModel(ServicesDbContext db, TunnelRegistry registry) : PageModel
{
    private const int PageSize = 25;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "created", "seen"];

    public ListView<InstallationRow> List { get; private set; } = default!;

    public async Task OnGetAsync()
    {
        var all = InstallationRow.Of(await db.Instances.AsNoTracking().ToListAsync(HttpContext.RequestAborted), registry);
        var q = ListQuery.Text(Request, "q");
        var kind = Pick(ListQuery.Text(Request, "kind"), "matos", "dyndns");
        var mode = Pick(ListQuery.Text(Request, "mode"), "tunnel", "direct");
        var tunnel = Pick(ListQuery.Text(Request, "tunnel"), "yes", "no");
        var blocked = Pick(ListQuery.Text(Request, "blocked"), "yes", "no");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<InstallationRow> query = all;
        if (q is not null)
        {
            // The name, an address, the version: what an administrator has in hand when somebody writes about abuse.
            query = query.Where(r => r.Instance.Name.Contains(q, Ic) || (r.Instance.PublicIpv4 ?? "").Contains(q, Ic)
                                     || (r.Instance.PublicIpv6 ?? "").Contains(q, Ic) || r.Instance.CreatedFromIp.Contains(q, Ic)
                                     || (r.Instance.Version ?? "").Contains(q, Ic));
        }

        if (kind is not null)
        {
            query = query.Where(r => r.Instance.DynDnsOnly == (kind == "dyndns"));
        }

        if (mode is not null)
        {
            query = query.Where(r => r.Instance.Mode == (mode == "direct" ? InstanceMode.Direct : InstanceMode.Tunnel));
        }

        if (tunnel is not null)
        {
            query = query.Where(r => (r.Tunnel is not null) == (tunnel == "yes"));
        }

        if (blocked is not null)
        {
            query = query.Where(r => r.Instance.Blocked == (blocked == "yes"));
        }

        query = sort switch
        {
            "created" => descending ? query.OrderByDescending(r => r.Instance.CreateDate) : query.OrderBy(r => r.Instance.CreateDate),
            "seen" => descending
                ? query.OrderByDescending(r => r.Instance.LastSeen ?? DateTime.MinValue)
                : query.OrderBy(r => r.Instance.LastSeen ?? DateTime.MinValue),
            _ => descending ? query.OrderByDescending(r => r.Instance.Name, StringComparer.Ordinal) : query.OrderBy(r => r.Instance.Name, StringComparer.Ordinal),
        };

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "installations",
            Action = "/admin/Installations",
            Label = "Installations",
            Search = q,
            SearchPlaceholder = "Name, address or version",
            SearchField = true,
            Filters =
            [
                new FilterField("kind", "Kind", [new("matos", "MatOS"), new("dyndns", "DynDNS")], kind),
                new FilterField("mode", "Mode", [new("tunnel", "Tunnel"), new("direct", "Direct")], mode),
                new FilterField("tunnel", "Tunnel connected", [new("yes", "Yes"), new("no", "No")], tunnel),
                new FilterField("blocked", "Blocked", [new("yes", "Yes"), new("no", "No")], blocked),
            ],
            SortOptions = [new("name", "Name"), new("created", "Created"), new("seen", "Last seen")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "installation" : "installations",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<InstallationRow> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    /// <summary>The value when it is one of <paramref name="allowed"/>; anything else is no filter.</summary>
    private static string? Pick(string? value, params string[] allowed) => value is not null && allowed.Contains(value) ? value : null;
}
