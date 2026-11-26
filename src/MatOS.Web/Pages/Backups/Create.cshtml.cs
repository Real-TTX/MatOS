using System.ComponentModel.DataAnnotations;
using MatOS.Core.Apps;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Backups;

public class CreateModel : PageModel
{
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly SystemConfigService _config;

    public CreateModel(AppService apps, JobService jobs, SystemConfigService config)
    {
        _apps = apps;
        _jobs = jobs;
        _config = config;
    }

    public List<InstalledApp> Apps { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    [Required(ErrorMessage = "Please choose an app.")]
    public long? AppId { get; set; }

    [BindProperty]
    public bool StopContainers { get; set; } = true;

    [BindProperty]
    [StringLength(500)]
    public string? Note { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        StopContainers = (await _config.GetAsync(HttpContext.RequestAborted)).Backups.StopContainers;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        var app = Apps.FirstOrDefault(a => a.Id == AppId);
        if (app is null)
        {
            ModelState.AddModelError(nameof(AppId), "Please choose an app.");
            return Page();
        }

        var jobId = await _jobs.EnqueueAsync(
            JobKind.Backup,
            $"Back up {app.Name}",
            app,
            AppJobs.Backup(app.Id, BackupKind.Manual, StopContainers, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()),
            HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task LoadAsync()
        => Apps = (await _apps.ListAsync(HttpContext.RequestAborted)).Where(a => a.Lifecycle == AppLifecycle.Installed).ToList();
}
