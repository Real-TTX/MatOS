using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Store;

public class IndexModel : PageModel
{
    private const int PageSize = 12;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "category"];

    private readonly CatalogService _catalog;
    private readonly MatOSDbContext _db;

    public IndexModel(CatalogService catalog, MatOSDbContext db)
    {
        _catalog = catalog;
        _db = db;
    }

    public ListView<CatalogApp> List { get; private set; } = default!;

    public Dictionary<string, int> InstalledCounts { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        var all = _catalog.GetAll();
        var categories = all.Select(a => a.Category).Distinct().Order().ToList();
        InstalledCounts = (await _db.InstalledApps.GroupBy(a => a.CatalogId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(HttpContext.RequestAborted))
            .ToDictionary(x => x.Key, x => x.Count);

        var q = ListQuery.Text(Request, "q");
        var category = ListQuery.Text(Request, "category");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<CatalogApp> query = all;
        if (category is not null)
        {
            query = query.Where(a => a.Category.Equals(category, Ic));
        }

        if (q is not null)
        {
            query = query.Where(a => a.Name.Contains(q, Ic) || a.Tagline.Contains(q, Ic) || a.Description.Contains(q, Ic) || a.Category.Contains(q, Ic));
        }

        Func<CatalogApp, string> key = sort == "category" ? a => a.Category.ToLowerInvariant() : a => a.Name.ToLowerInvariant();
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "store",
            Action = "/Store",
            Label = "Store",
            Search = q,
            SearchPlaceholder = "Search apps",
            SearchField = true,
            Filters = [new FilterField("category", "Category", categories.Select(c => new FilterOption(c, c)).ToList(), category)],
            SortOptions = [new("name", "Name"), new("category", "Category")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "app" : "apps",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<CatalogApp> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    /// <summary>Picks up catalog files that were added to <c>/data/catalog</c> without a restart.</summary>
    public IActionResult OnPostReload()
    {
        _catalog.Reload();
        StatusMessage = $"Catalog reloaded: {_catalog.GetAll().Count} apps.";
        return RedirectToPage();
    }
}
