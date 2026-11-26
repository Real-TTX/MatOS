using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Web.Controls;
using MatOS.Web.Infrastructure;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "status", "added"];

    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly PersonAccess _person;
    private readonly AppUrls _urls;

    public IndexModel(AppService apps, CatalogService catalog, PersonAccess person, AppUrls urls)
    {
        _apps = apps;
        _catalog = catalog;
        _person = person;
        _urls = urls;
    }

    /// <summary>An installed app as the list shows it; <c>Address</c> is the one the browser opens, as a person reads it (without http://), or null.</summary>
    public sealed record Row(InstalledApp App, AppStatusView Status, string? Icon, string Kind, string? Address = null);

    public ListView<Row> List { get; private set; } = default!;

    public bool Busy => List.Items.Any(r => r.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        // A person sees only the apps MatIdentity lets them use (an administrator of MatOS all of them).
        var person = User.IsAdmin() ? null : await _person.ForUserAsync(User.GetUserId(), ct);
        var apps = (await _apps.ListAsync(ct)).Where(a => person is null || person.MayUse(a)).ToList();
        var runtimes = await _apps.GetRuntimesAsync(ct);

        var all = apps.Select(a => new Row(
            a,
            AppStatus.Of(a, runtimes.GetValueOrDefault(a.Slug)),
            _catalog.IconOf(a.CatalogId),
            a.Role switch
            {
                AppRoles.Proxy => "Addresses and certificates",
                AppRoles.Ddns => "Keeps your address current",
                AppRoles.Docker => "Runs your apps",
                AppRoles.Identity => "Single sign-on",
                AppRoles.Mail => "Mail relay",
                _ => _catalog.Get(a.CatalogId)?.Category ?? "App",
            })).ToList();

        var q = ListQuery.Text(Request, "q");
        var status = ListQuery.Text(Request, "status");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<Row> rows = all;
        if (q is not null)
        {
            rows = rows.Where(r => r.App.Name.Contains(q, Ic) || r.App.Slug.Contains(q, Ic) || r.Kind.Contains(q, Ic));
        }

        rows = status switch
        {
            "running" => rows.Where(r => r.Status.Css == "online"),
            "stopped" => rows.Where(r => r.Status.Css == "unknown"),
            "attention" => rows.Where(r => r.Status.Css is "offline" or "warn"),
            _ => rows,
        };

        Func<Row, object> key = sort switch
        {
            "status" => r => r.Status.Css,
            "added" => r => r.App.CreateDate,
            _ => r => r.App.Name.ToLowerInvariant(),
        };
        var list = (descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(r => r.App.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "apps",
            Action = "/Apps",
            Label = "Apps",
            Search = q,
            SearchPlaceholder = "Search apps",
            SearchField = true,
            Filters =
            [
                new FilterField("status", "Status", [new("running", "Running"), new("stopped", "Stopped"), new("attention", "Needs attention")], status),
            ],
            SortOptions = [new("name", "Name"), new("status", "Status"), new("added", "Added")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "app" : "apps",
        };

        var (page, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);

        // Every app shows its address the same way (host:port, or the name the reverse proxy gave it), only for the rows of this page.
        var items = new List<Row>(page.Count);
        foreach (var row in page)
        {
            var url = await _urls.OpenUrlAsync(row.App, Request, ct, path: string.Empty);
            items.Add(row with { Address = url?.Replace("https://", "").Replace("http://", "").TrimEnd('/') });
        }

        List = new ListView<Row> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}
