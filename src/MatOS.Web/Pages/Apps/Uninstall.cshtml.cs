using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class UninstallModel : PageModel
{
    private readonly AppService _apps;
    private readonly JobService _jobs;

    public UninstallModel(AppService apps, JobService jobs)
    {
        _apps = apps;
        _jobs = jobs;
    }

    public InstalledApp App { get; private set; } = null!;

    /// <summary>Also delete the volumes. Off by default: removing the app keeps its data, so installing it again picks it up.</summary>
    [BindProperty]
    public bool DeleteData { get; set; }

    [BindProperty]
    public bool BackupFirst { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null)
        {
            return NotFound();
        }

        if (IsProtected(app))
        {
            return RedirectToPage("/Apps/Details", new { id });
        }

        App = app;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null)
        {
            return NotFound();
        }

        if (IsProtected(app))
        {
            return RedirectToPage("/Apps/Details", new { id });
        }

        var jobId = await _jobs.EnqueueAsync(
            JobKind.Uninstall,
            $"Uninstall {app.Name}",
            app,
            AppJobs.Uninstall(app.Id, DeleteData, DeleteData && BackupFirst),
            HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    /// <summary>The engines are part of MatOS and stay; only a failed install can be removed to start over.</summary>
    private static bool IsProtected(InstalledApp app) => app.IsCore && app.Lifecycle != AppLifecycle.Failed;
}
