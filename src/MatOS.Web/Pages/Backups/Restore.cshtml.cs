using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Backups;

public class RestoreModel : PageModel
{
    private readonly MatOSDbContext _db;
    private readonly BackupHub _backups;
    private readonly JobService _jobs;

    public RestoreModel(MatOSDbContext db, BackupHub backups, JobService jobs)
    {
        _db = db;
        _backups = backups;
        _jobs = jobs;
    }

    public BackupItem Backup { get; private set; } = null!;

    /// <summary>The app as it exists now; <c>null</c> when it was uninstalled (the restore re-creates it).</summary>
    public InstalledApp? App { get; private set; }

    [BindProperty]
    public bool BackupFirst { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(string id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var jobId = await _jobs.EnqueueAsync(
            JobKind.Restore,
            $"Restore {Backup.AppName} from {Stamp(Backup)}",
            App,
            AppJobs.Restore(Backup.Id, App is not null && BackupFirst),
            HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync(string id)
    {
        var backup = await _backups.GetAsync(id, HttpContext.RequestAborted);
        if (backup is null || backup.Status != BackupStatus.Succeeded)
        {
            return false;
        }

        Backup = backup;
        App = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == backup.AppSlug, HttpContext.RequestAborted);
        return true;
    }

    private static string Stamp(BackupItem backup) => backup.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
