using System.Globalization;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Store;

/// <summary>
/// The store. The plain first look (no search, no filter, no sort of one's own) is an app store: a spotlight on one Mat app with a picture, the row
/// "Made for MatOS" and a shelf per category. A search, a filter or a sort shows the gallery of what matches, with the pager.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 12;

    /// <summary>How many apps "Made for MatOS" picks: one for the spotlight, the others for the row under it.</summary>
    private const int FeaturedCount = 9;

    /// <summary>How many apps a shelf of a category shows before "See all".</summary>
    public const int ShelfSize = 4;

    /// <summary>The category of the parts MatOS runs on and of the apps of others that do the same: its shelf comes last.</summary>
    private const string SystemCategory = "System";

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

    /// <summary>"Made for MatOS": the Mat apps that sign in through MatOS, shown on the plain first look only.</summary>
    public IReadOnlyList<CatalogApp> Featured { get; private set; } = [];

    /// <summary>The big card on top of the plain first look: one Mat app with a picture, another one each day.</summary>
    public CatalogApp? Spotlight { get; private set; }

    /// <summary>The plain first look: the apps on shelves, one per category, instead of one long gallery.</summary>
    public bool Plain { get; private set; }

    /// <summary>The shelves of the plain first look: a category and its apps (the first <see cref="ShelfSize"/> are shown).</summary>
    public IReadOnlyList<StoreShelf> Shelves { get; private set; } = [];

    /// <summary>The categories of the catalog: a row of links over the apps.</summary>
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

        // The plain look (no search, no filter, no sort of one's own): the spotlight, the Mat apps, then a shelf per category.
        var plain = q is null && category is null && state is null && chosenSort is null;
        Plain = plain;

        Func<CatalogApp, string> key = sort switch
        {
            "category" => a => a.Category.ToLowerInvariant(),
            "state" => a => AppStates.Rank(a.State).ToString("D2", CultureInfo.InvariantCulture),
            _ => a => a.Name.ToLowerInvariant(),
        };
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

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

        if (plain)
        {
            var featured = PickFeatured(all);
            Spotlight = PickSpotlight(featured, DateOnly.FromDateTime(DateTime.Today));
            // The app of the spotlight is said once: not again in the row under it.
            Featured = featured.Where(a => a != Spotlight).ToList();
            Shelves = Shelve(list);
        }
    }

    /// <summary>
    /// The shelves: one per category, the categories with the most apps first and System (the parts MatOS runs on, the apps of others that do the same) last;
    /// on a shelf the apps that are furthest along first.
    /// </summary>
    public static IReadOnlyList<StoreShelf> Shelve(IEnumerable<CatalogApp> apps) => apps
        .GroupBy(a => a.Category, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key.Equals(SystemCategory, Ic))
        .ThenByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new StoreShelf(g.Key, g.OrderBy(a => AppStates.Rank(a.State)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList()))
        .ToList();

    /// <summary>
    /// The app of the spotlight: one of "Made for MatOS" that has a picture, another one each day (by the day of the year), so the first look changes and still is
    /// the same all day. The row puts the apps not installed yet first, and so does the spotlight.
    /// </summary>
    public static CatalogApp? PickSpotlight(IReadOnlyList<CatalogApp> featured, DateOnly day)
    {
        var candidates = featured.Where(a => a.Screenshots.Count > 0).ToList();
        return candidates.Count == 0 ? featured.FirstOrDefault() : candidates[day.DayOfYear % candidates.Count];
    }

    /// <summary>What an app does with MatOS, as small marks on its card: the one sign-in and the mail.</summary>
    public static IReadOnlyList<StoreMark> MarksFor(CatalogApp app)
    {
        var marks = new List<StoreMark>(2);
        if (app.Uses(Capabilities.IdentityOidc))
        {
            marks.Add(new StoreMark("key", "One sign-in with MatOS"));
        }

        if (app.Uses(Capabilities.MailSmtp))
        {
            marks.Add(new StoreMark("mail", "Sends its mail through MatOS"));
        }

        return marks;
    }

    /// <summary>"Made for MatOS": the apps that sign in through MatOS and do the most with it, those not installed yet first.</summary>
    private List<CatalogApp> PickFeatured(IEnumerable<CatalogApp> apps) => apps
        .Where(a => a.Role == AppRoles.App && a.State is AppStates.Integrated or AppStates.Beta && a.Uses(Capabilities.IdentityOidc))
        .OrderBy(a => Installed.ContainsKey(a.Id))
        .ThenBy(a => AppStates.Rank(a.State))
        .ThenByDescending(a => a.Consumes.Count)
        .ThenByDescending(a => a.Screenshots.Count > 0)
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
            ? new StoreAction(StoreActionKind.Install, "Get", $"/Store/Install?id={app.Id}", $"Install {app.Name}")
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

/// <summary>A shelf of the plain first look: a category and its apps, in the order of the shelf.</summary>
public sealed record StoreShelf(string Category, IReadOnlyList<CatalogApp> Apps);

/// <summary>A small mark on the card of an app: an icon of the sprite and what it means (its tip and its words for a screen reader).</summary>
public sealed record StoreMark(string Icon, string Label);

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
