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

    /// <summary>What came of a job that went well, in one plain sentence (the line over the buttons).</summary>
    public string Result
    {
        get
        {
            var name = App?.Name ?? "The app";
            return Job.Kind switch
            {
                JobKind.Install => $"{name} is installed.",
                JobKind.Update => $"{name} is up to date.",
                JobKind.Uninstall => $"{name} is removed.",
                JobKind.Start => $"{name} is running.",
                JobKind.Stop => $"{name} is stopped.",
                JobKind.Restart => $"{name} has started again.",
                JobKind.Backup => $"{name} is backed up.",
                JobKind.Restore => $"{name} is restored from the backup.",
                JobKind.Move => $"{name} runs on its new node.",
                JobKind.Redeploy => $"{name} runs with its new settings.",
                JobKind.Provision => $"{name} is set up.",
                _ => "Done.",
            };
        }
    }
}
