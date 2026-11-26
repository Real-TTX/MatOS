using System.Globalization;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Store;

public class IndexModel : PageModel
{
    private const int PageSize = 12;

    /// <summary>How many apps "Start here" shows.</summary>
    private const int FeaturedCount = 4;

    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "category", "state"];

    private readonly CatalogService _catalog;
    private readonly MatOSDbContext _db;

    public IndexModel(CatalogService catalog, MatOSDbContext db)
    {
        _catalog = catalog;
        _db = db;
    }

    public ListView<CatalogApp> List { get; private set; } = default!;

    /// <summary>What is installed of each catalog entry (an app can be installed more than once), oldest first.</summary>
    public Dictionary<string, List<StoreInstall>> Installed { get; private set; } = new();

    /// <summary>"Start here": a few Mat apps that work together with MatOS, shown on the plain first page only.</summary>
    public IReadOnlyList<CatalogApp> Featured { get; private set; } = [];

    /// <summary>The categories of the catalog: a row of links over the gallery.</summary>
    public IReadOnlyList<string> Categories { get; private set; } = [];

    /// <summary>The category the gallery shows; null = all.</summary>
    public string? Category { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        // The engines are the parts MatOS runs on, not apps to pick: they live in the folder System of the home screen and in the settings (both link to their page here).
        var all = _catalog.GetAll().Where(a => !a.Core).ToList();
        var categories = all.Select(a => a.Category).Distinct().Order().ToList();
        Categories = categories;
        Installed = (await _db.InstalledApps.OrderBy(a => a.Id).Select(a => new StoreInstall(a.Id, a.CatalogId, a.Lifecycle, a.UiService != null)).ToListAsync(HttpContext.RequestAborted))
            .GroupBy(a => a.CatalogId).ToDictionary(g => g.Key, g => g.ToList());

        var q = ListQuery.Text(Request, "q");
        var category = ListQuery.Text(Request, "category") is { } c ? categories.FirstOrDefault(x => x.Equals(c, Ic)) ?? c : null;
        Category = category;
        var state = ListQuery.Text(Request, "state") is { } st && AppStates.IsKnown(st) ? st : null;
        var chosenSort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : null;
        var sort = chosenSort ?? "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<CatalogApp> query = all;
        if (state is not null)
        {
            query = query.Where(a => a.State == state);
        }

        if (category is not null)
        {
            query = query.Where(a => a.Category.Equals(category, Ic));
        }

        if (q is not null)
        {
            query = query.Where(a => a.Name.Contains(q, Ic) || a.Tagline.Contains(q, Ic) || a.Description.Contains(q, Ic) || a.Category.Contains(q, Ic) || AppStates.Label(a.State).Contains(q, Ic));
        }

        // "Start here" is for the plain look (no search, no filter); its apps then come last in the gallery, so the first screen does not show them twice.
        var plain = q is null && category is null && state is null;
        var featured = plain ? PickFeatured(all) : [];
        var featuredIds = featured.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Func<CatalogApp, string> key = sort switch
        {
            "category" => a => a.Category.ToLowerInvariant(),
            "state" => a => AppStates.Rank(a.State).ToString("D2", CultureInfo.InvariantCulture),
            _ => a => a.Name.ToLowerInvariant(),
        };
        var ordered = query.OrderBy(a => chosenSort is null && featuredIds.Contains(a.Id));
        var list = (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "store",
            Action = "/Store",
            Label = "Store",
            Search = q,
            SearchPlaceholder = "Search apps",
            SearchField = true,
            Filters =
            [
                new FilterField("category", "Category", categories.Select(c => new FilterOption(c, c)).ToList(), category),
                new FilterField("state", "State", AppStates.All.Select(s => new FilterOption(s, AppStates.Label(s))).ToList(), state),
            ],
            SortOptions = [new("name", "Name"), new("category", "Category"), new("state", "State")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "app" : "apps",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<CatalogApp> { Items = items, Toolbar = toolbar, Pager = pager };

        // A search, a filter or another page is about what the person looks for: "Start here" is for the plain first look.
        if (ListQuery.Page(Request) <= 1)
        {
            Featured = featured;
        }
    }

    /// <summary>"Start here": the Mat apps that do the most with MatOS, those not installed yet first.</summary>
    private List<CatalogApp> PickFeatured(IEnumerable<CatalogApp> apps) => apps
        .Where(a => a.Role == AppRoles.App && a.State is AppStates.Integrated or AppStates.Beta)
        .OrderBy(a => Installed.ContainsKey(a.Id))
        .ThenBy(a => AppStates.Rank(a.State))
        .ThenByDescending(a => a.Consumes.Count)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .Take(FeaturedCount)
        .ToList();

    /// <summary>The address of the gallery with only <paramref name="category"/> (null = every category); the search, the state and the sort are kept.</summary>
    public string CategoryUrl(string? category)
    {
        var url = List.Toolbar.UrlWithout("category");
        return category is null ? url : url + (url.Contains('?') ? "&" : "?") + "category=" + Uri.EscapeDataString(category);
    }

    /// <summary>The button of an app: Open when it is installed, where it stands when it is on its way (or failed), else Install.</summary>
    public StoreAction ActionFor(CatalogApp app)
    {
        if (Installed.TryGetValue(app.Id, out var copies))
        {
            if (copies.FirstOrDefault(c => c.Lifecycle == AppLifecycle.Installed && c.HasUi) is { } open)
            {
                return new StoreAction(StoreActionKind.Open, "Open", $"/Apps/Open?id={open.Id}", $"Open {app.Name}");
            }

            var copy = copies[^1];
            var label = copy.Lifecycle switch
            {
                AppLifecycle.Installing => "Installing",
                AppLifecycle.Failed => "Retry",
                AppLifecycle.Removing => "Removing",
                _ => "Details",
            };
            return new StoreAction(StoreActionKind.Status, label, $"/Apps/Details?id={copy.Id}", $"{app.Name}: {label}");
        }

        return AppStates.CanInstall(app.State)
            ? new StoreAction(StoreActionKind.Install, "Install", $"/Store/Install?id={app.Id}", $"Install {app.Name}")
            : new StoreAction(StoreActionKind.Soon, "Soon", $"/Store/Install?id={app.Id}", $"{app.Name}: coming soon");
    }

    /// <summary>Picks up catalog files that were added to <c>/data/catalog</c> without a restart.</summary>
    public IActionResult OnPostReload()
    {
        _catalog.Reload();
        StatusMessage = $"Catalog reloaded: {_catalog.GetAll().Count} apps.";
        return RedirectToPage();
    }
}

/// <summary>One installed copy of a catalog entry, as far as the store needs it.</summary>
public sealed record StoreInstall(long Id, string CatalogId, AppLifecycle Lifecycle, bool HasUi);

public enum StoreActionKind
{
    Install,
    Open,
    Status,
    Soon,
}

/// <summary>The button of an app in the store: what it does, what it says, where it goes and its name for a screen reader.</summary>
public sealed record StoreAction(StoreActionKind Kind, string Label, string Href, string AriaLabel);
