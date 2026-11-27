using MatOS.Core.Volumes;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Volumes;

/// <summary>
/// The volumes (docs/ARCHITECTURE.md "Volumes and the file explorer"): those of the apps and MatOS's own (on this machine or a network share), with what they are,
/// who uses them and how big they are. Each opens in the file explorer; MatOS's own can be changed, tested and removed.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 20;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] Sorts = ["name", "kind", "size", "app"];

    private readonly VolumeService _volumes;

    public IndexModel(VolumeService volumes)
    {
        _volumes = volumes;
    }

    public ListView<VolumeView>? List { get; private set; }

    /// <summary>Volumes cannot be used now: MatDock does not answer, or it is older than the volumes of its API.</summary>
    public bool Unavailable { get; private set; }

    /// <summary>MatDock answered, but not with the list (the reason).</summary>
    public string? Problem { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Unavailable = !await _volumes.IsAvailableAsync(ct);
        var belongs = ListQuery.Text(Request, "belongs");
        List<VolumeView> all = [];
        if (!Unavailable)
        {
            try
            {
                // Those of MatOS and its apps; "Everything on this machine" lists the others of the host too.
                all = await _volumes.ListAsync(sizes: true, ct, all: belongs == "host");
            }
            catch (InvalidOperationException ex)
            {
                Problem = VolumeService.Reason(ex);
            }
        }

        var q = ListQuery.Text(Request, "q");
        var kindText = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") is { } s && Sorts.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: sort == "size");

        IEnumerable<VolumeView> query = all;
        if (Enum.TryParse<VolumeKind>(kindText, out var kind))
        {
            query = query.Where(v => v.Kind == kind);
        }
        else
        {
            kindText = null;
        }

        query = belongs switch
        {
            "own" => query.Where(v => v.Ours),
            "apps" => query.Where(v => v.Owner is not null),
            _ => query,
        };
        if (belongs is not ("own" or "apps" or "host"))
        {
            belongs = null;
        }

        if (q is not null)
        {
            query = query.Where(v => v.Name.Contains(q, Ic) || (v.Description?.Contains(q, Ic) ?? false) || (v.Owner?.Name.Contains(q, Ic) ?? false)
                || v.Users.Any(u => u.Contains(q, Ic)) || v.Address.Contains(q, Ic));
        }

        Func<VolumeView, object> key = sort switch
        {
            "kind" => v => v.KindLabel,
            "size" => v => v.SizeBytes ?? -1,
            "app" => v => (v.Owner?.Name ?? v.Users.FirstOrDefault() ?? "~").ToLowerInvariant(),
            _ => v => v.Name.ToLowerInvariant(),
        };
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "volumes",
            Action = "/Volumes",
            Label = "Volumes",
            Search = q,
            SearchPlaceholder = "Search volumes",
            SearchField = true,
            Filters =
            [
                new FilterField("kind", "Kind", all.Select(v => v.Kind).Distinct().Order()
                    .Select(k => new FilterOption(k.ToString(), VolumeView.LabelOf(k))).ToList(), kindText),
                new FilterField("belongs", "Belongs to", [new FilterOption("own", "MatOS (your own)"), new FilterOption("apps", "An app"), new FilterOption("host", "Everything on this machine")], belongs),
            ],
            SortOptions = [new("name", "Name"), new("kind", "Kind"), new("app", "App"), new("size", "Size")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "volume" : "volumes",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<VolumeView> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    /// <summary>Tests a volume from the list: mounted in a helper, listed and written to.</summary>
    public async Task<IActionResult> OnPostTestAsync(string name)
    {
        var (ok, message) = await _volumes.TestAsync(name, HttpContext.RequestAborted);
        StatusMessage = ok ? $"{name}: {message}" : $"{name} does not work: {message}";
        IsError = !ok;
        return RedirectToPage();
    }
}
