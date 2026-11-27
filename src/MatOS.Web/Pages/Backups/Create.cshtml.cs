using System.ComponentModel.DataAnnotations;
using MatOS.Core.Apps;
using MatOS.Core.Backups;
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
    private readonly BackupTargetService _targets;

    public CreateModel(AppService apps, JobService jobs, SystemConfigService config, BackupTargetService targets)
    {
        _apps = apps;
        _jobs = jobs;
        _config = config;
        _targets = targets;
    }

    /// <summary>The targets a backup can be kept at besides this machine (empty: the choice is not shown).</summary>
    public List<BackupTarget> Targets { get; private set; } = new();

    /// <summary>Where the backup goes: "" the default target, "0" this machine, else a target's id (<see cref="BackupDestination.Parse"/>).</summary>
    [BindProperty]
    public string? Destination { get; set; }

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
        Destination = Targets.FirstOrDefault(t => t.IsDefault)?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
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
            AppJobs.Backup(app.Id, BackupKind.Manual, StopContainers, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(), BackupDestination.Parse(Destination)),
            HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task LoadAsync()
    {
        Apps = (await _apps.ListAsync(HttpContext.RequestAborted)).Where(a => a.Lifecycle == AppLifecycle.Installed).ToList();
        Targets = await _targets.ListAsync(HttpContext.RequestAborted);
    }
}
