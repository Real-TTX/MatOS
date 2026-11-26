using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Jobs;

public class IndexModel : PageModel
{
    private const int PageSize = 15;

    private readonly MatOSDbContext _db;

    public IndexModel(MatOSDbContext db)
    {
        _db = db;
    }

    public ListView<Job> List { get; private set; } = default!;

    public bool Busy => List.Items.Any(j => j.Status is JobStatus.Queued or JobStatus.Running);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var q = ListQuery.Text(Request, "q");
        var statusText = ListQuery.Text(Request, "status");
        var descending = ListQuery.Descending(Request, defaultDescending: true);

        var total = await _db.Jobs.CountAsync(ct);
        var query = _db.Jobs.AsNoTracking().AsQueryable();

        if (Enum.TryParse<JobStatus>(statusText, out var status))
        {
            query = query.Where(j => j.Status == status);
        }
        else
        {
            statusText = null;
        }

        if (q is not null)
        {
            query = query.Where(j => EF.Functions.Like(j.Title, $"%{q}%") || (j.AppSlug != null && EF.Functions.Like(j.AppSlug, $"%{q}%")));
        }

        query = descending ? query.OrderByDescending(j => j.Id) : query.OrderBy(j => j.Id);

        var matching = await query.CountAsync(ct);
        var toolbar = new ToolbarModel
        {
            Id = "jobs",
            Action = "/Jobs",
            Label = "Activity",
            Search = q,
            SearchPlaceholder = "Search activity",
            SearchField = true,
            Filters = [new FilterField("status", "Status", Enum.GetValues<JobStatus>().Select(s => new FilterOption(s.ToString(), s.ToString())).ToList(), statusText)],
            SortOptions = [new("started", "Started")],
            Sort = "started",
            Descending = descending,
            DefaultSort = "started",
            DefaultDescending = true,
            Total = total,
            Matching = matching,
            Noun = total == 1 ? "task" : "tasks",
        };

        var pages = Math.Max(1, (int)Math.Ceiling(matching / (double)PageSize));
        var page = Math.Clamp(ListQuery.Page(Request), 1, pages);
        var items = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        List = new ListView<Job>
        {
            Items = items,
            Toolbar = toolbar,
            Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = toolbar.UrlWithPage },
        };
    }
}
