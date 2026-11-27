using MatOS.Core.Backups;
using MatOS.Core.Data;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Backups;

/// <summary>The backup targets: an SFTP folder, a WebDAV folder or a network share where backups are kept besides this machine.</summary>
public class TargetsModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] Sorts = ["name", "kind", "backups"];

    private readonly BackupTargetService _targets;
    private readonly MatOSDbContext _db;

    public TargetsModel(BackupTargetService targets, MatOSDbContext db)
    {
        _targets = targets;
        _db = db;
    }

    public ListView<BackupTarget>? List { get; private set; }

    /// <summary>Targets cannot be used right now (the engine of the backups does not answer, or is too old).</summary>
    public bool Unavailable { get; private set; }

    /// <summary>No app is run by the engine that keeps targets: backups stay on this machine whatever is chosen.</summary>
    public bool NoAppUsesThem { get; private set; }

    public TabBarModel Tabs => IndexModel.TabsFor("targets");

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Unavailable = !await _targets.IsAvailableAsync(ct);
        NoAppUsesThem = !Unavailable && !await _db.InstalledApps.AnyAsync(a => a.Engine == "matdock", ct);
        List<BackupTarget> all = Unavailable ? [] : await _targets.ListAsync(ct);

        var q = ListQuery.Text(Request, "q");
        var kindText = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") is { } s && Sorts.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<BackupTarget> query = all;
        if (Enum.TryParse<BackupTargetKind>(kindText, out var kind))
        {
            query = query.Where(t => t.Kind == kind);
        }
        else
        {
            kindText = null;
        }

        if (q is not null)
        {
            query = query.Where(t => t.Name.Contains(q, Ic) || t.Address.Contains(q, Ic));
        }

        Func<BackupTarget, object> key = sort switch
        {
            "kind" => t => t.KindLabel,
            "backups" => t => t.BackupCount,
            _ => t => t.Name.ToLowerInvariant(),
        };
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "targets",
            Action = "/Backups/Targets",
            Label = "Targets",
            Search = q,
            SearchPlaceholder = "Search targets",
            SearchField = true,
            Filters =
            [
                new FilterField("kind", "Kind", all.Select(t => t.Kind).Distinct().Order()
                    .Select(k => new FilterOption(k.ToString(), BackupTarget.LabelOf(k))).ToList(), kindText),
            ],
            SortOptions = [new("name", "Name"), new("kind", "Kind"), new("backups", "Backups")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "target" : "targets",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<BackupTarget> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    /// <summary>Tests a saved target from the list.</summary>
    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        var (ok, message) = await _targets.TestAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? message : $"The test failed: {message}";
        IsError = !ok;
        return RedirectToPage();
    }
}
