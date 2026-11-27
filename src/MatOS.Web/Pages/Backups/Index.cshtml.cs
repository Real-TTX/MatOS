using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Backups;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] BackupSorts = ["created", "size", "app"];
    private static readonly string[] ScheduleSorts = ["name", "app"];

    private readonly MatOSDbContext _db;
    private readonly BackupHub _backups;
    private readonly CatalogService _catalog;

    public IndexModel(MatOSDbContext db, BackupHub backups, CatalogService catalog)
    {
        _db = db;
        _backups = backups;
        _catalog = catalog;
    }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public bool SchedulesTab => Tab == "schedules";

    public ListView<BackupItem>? History { get; private set; }

    public ListView<ScheduleItem>? Schedules { get; private set; }

    public Dictionary<string, string> AppNames { get; private set; } = new();

    public long TotalSize { get; private set; }

    /// <summary>MatDock runs apps but does not answer: its backups and schedules cannot be listed right now.</summary>
    public bool MatDockSilent { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public TabBarModel Tabs => TabsFor(SchedulesTab ? "schedules" : "history");

    /// <summary>The tabs of the backups: History, Schedules and Targets (its own page, /Backups/Targets).</summary>
    public static TabBarModel TabsFor(string active) => new()
    {
        Tabs =
        [
            new TabItem { Title = "History", Href = "/Backups", Icon = "clock", Active = active == "history" },
            new TabItem { Title = "Schedules", Href = "/Backups?tab=schedules", Icon = "calendar", Active = active == "schedules" },
            new TabItem { Title = "Targets", Href = "/Backups/Targets", Icon = "hard-drive", Active = active == "targets" },
        ],
    };

    public string? IconOf(string catalogId) => _catalog.IconOf(catalogId);

    /// <summary>Where a backup or schedule is kept, as the label the lists show.</summary>
    public static string EngineLabel(string engine) => engine == "matdock" ? "MatDock" : "MatOS";

    public string AppNameOf(string? slug) => slug is null ? "All apps" : AppNames.GetValueOrDefault(slug, "(removed)");

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        AppNames = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Slug, a => a.Name, ct);
        MatDockSilent = await _db.InstalledApps.AnyAsync(a => a.Engine == "matdock", ct) && !await _backups.ForEngine("matdock").IsAvailableAsync(ct);

        if (SchedulesTab)
        {
            await BuildSchedulesAsync(ct);
        }
        else
        {
            await BuildHistoryAsync(ct);
        }
    }

    private async Task BuildHistoryAsync(CancellationToken ct)
    {
        var all = await _backups.ListAsync(ct);
        var q = ListQuery.Text(Request, "q");
        var app = ListQuery.Text(Request, "app");
        var kindText = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") is { } s && BackupSorts.Contains(s) ? s : "created";
        var descending = ListQuery.Descending(Request, defaultDescending: sort != "app");

        IEnumerable<BackupItem> query = all;
        if (app is not null)
        {
            query = query.Where(b => b.AppSlug == app);
        }

        if (Enum.TryParse<BackupKind>(kindText, out var kind))
        {
            query = query.Where(b => b.Kind == kind);
        }
        else
        {
            kindText = null;
        }

        if (q is not null)
        {
            query = query.Where(b => b.AppName.Contains(q, Ic) || (b.Note?.Contains(q, Ic) ?? false));
        }

        var filtered = query.ToList();
        TotalSize = filtered.Where(b => b.Status == BackupStatus.Succeeded).Sum(b => b.SizeBytes);

        Func<BackupItem, object> key = sort switch
        {
            "size" => b => b.SizeBytes,
            "app" => b => b.AppName.ToLowerInvariant(),
            _ => b => b.CreatedUtc,
        };
        var list = (descending ? filtered.OrderByDescending(key) : filtered.OrderBy(key)).ThenByDescending(b => b.CreatedUtc).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "backups",
            Action = "/Backups",
            Label = "Backups",
            Search = q,
            SearchPlaceholder = "Search backups",
            SearchField = true,
            Filters =
            [
                new FilterField("app", "App", all.Select(b => b.AppSlug).Distinct().Order().Select(slug => new FilterOption(slug, AppNames.GetValueOrDefault(slug, slug))).ToList(), app),
                new FilterField("kind", "Kind", Enum.GetValues<BackupKind>().Select(k => new FilterOption(k.ToString(), k.ToString())).ToList(), kindText),
            ],
            SortOptions = [new("created", "Created"), new("size", "Size"), new("app", "App")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "created",
            DefaultDescending = true,
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "backup" : "backups",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        History = new ListView<BackupItem> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    private async Task BuildSchedulesAsync(CancellationToken ct)
    {
        var all = await _backups.ListSchedulesAsync(ct);
        var q = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is { } s && ScheduleSorts.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<ScheduleItem> query = all;
        if (q is not null)
        {
            query = query.Where(x => x.Name.Contains(q, Ic) || AppNameOf(x.AppSlug).Contains(q, Ic));
        }

        Func<ScheduleItem, string> key = sort == "app" ? x => AppNameOf(x.AppSlug).ToLowerInvariant() : x => x.Name.ToLowerInvariant();
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "schedules",
            Action = "/Backups",
            Label = "Schedules",
            Search = q,
            SearchPlaceholder = "Search schedules",
            SearchField = true,
            SortOptions = [new("name", "Name"), new("app", "App")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Keep = new Dictionary<string, string?> { ["tab"] = "schedules" },
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "schedule" : "schedules",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        Schedules = new ListView<ScheduleItem> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        await _backups.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "The backup was deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteScheduleAsync(string id)
    {
        await _backups.DeleteScheduleAsync(id, HttpContext.RequestAborted);
        StatusMessage = "The schedule was deleted. Existing backups are kept.";
        return RedirectToPage(new { tab = "schedules" });
    }

    /// <summary>Streams a backup as a tar file, so it can be copied off the host.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(string id)
    {
        var download = await _backups.OpenDownloadAsync(id, HttpContext.RequestAborted);
        if (download is null)
        {
            return NotFound();
        }

        Response.ContentType = "application/x-tar";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{download.FileName}\"";
        await download.WriteToAsync(Response.Body, HttpContext.RequestAborted);
        return new EmptyResult();
    }
}
