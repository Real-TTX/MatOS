using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Jobs;

public class DetailsModel : PageModel
{
    private readonly JobService _jobs;
    private readonly AppService _apps;

    public DetailsModel(JobService jobs, AppService apps)
    {
        _jobs = jobs;
        _apps = apps;
    }

    public Job Job { get; private set; } = null!;

    /// <summary>The app the job is about, if it still exists.</summary>
    public InstalledApp? App { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var job = await _jobs.GetAsync(id, HttpContext.RequestAborted);
        if (job is null)
        {
            return NotFound();
        }

        Job = job;
        if (job.AppId is { } appId)
        {
            App = await _apps.GetAsync(appId, HttpContext.RequestAborted);
        }

        return Page();
    }
}
