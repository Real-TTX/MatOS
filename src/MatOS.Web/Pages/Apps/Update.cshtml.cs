using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class UpdateModel : PageModel
{
    private readonly AppService _apps;
    private readonly JobService _jobs;

    public UpdateModel(AppService apps, JobService jobs)
    {
        _apps = apps;
        _jobs = jobs;
    }

    public InstalledApp App { get; private set; } = null!;

    [BindProperty]
    public bool BackupFirst { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null || app.Lifecycle != AppLifecycle.Installed)
        {
            return NotFound();
        }

        App = app;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null || app.Lifecycle != AppLifecycle.Installed)
        {
            return NotFound();
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Update, $"Update {app.Name}", app, AppJobs.Update(app.Id, BackupFirst), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }
}
