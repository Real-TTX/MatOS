using MatOS.Core;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Core.Updates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Settings;

/// <summary>
/// Settings, Updates (docs/ARCHITECTURE.md "How MatOS updates itself"): whether a newer MatOS is there and *Update MatOS*, the automatic updates (off, the
/// apps and engines, everything) with their nightly window and the backup first, and how the last ones ended.
/// </summary>
public class UpdatesModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SelfUpdateService _self;
    private readonly JobService _jobs;
    private readonly MatOSDbContext _db;

    private readonly HostUpdateService _host;

    public UpdatesModel(SystemConfigService config, SelfUpdateService self, JobService jobs, MatOSDbContext db, HostUpdateService host)
    {
        _config = config;
        _self = self;
        _jobs = jobs;
        _db = db;
        _host = host;
    }

    public sealed class InputModel
    {
        public string? Mode { get; set; } = UpdateMode.Recommended;

        /// <summary>The start of the window (<c>HH:mm</c>); posted only while the mode is not off (a dependent field), so never required by binding.</summary>
        public string? WindowStart { get; set; } = "03:00";

        public bool BackupFirst { get; set; } = true;

        /// <summary>The night installs the updates of this computer too (off unless chosen).</summary>
        public bool HostUpdates { get; set; }

        /// <summary>And restarts it when an update needs it (off unless chosen).</summary>
        public bool HostRestart { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public UpdateSettings Settings { get; private set; } = new();

    public string Version => AppVersion.Current;

    /// <summary>The job of a self-update that waits or runs, or null.</summary>
    public Job? Running { get; private set; }

    /// <summary>MatOS is on its way to a new version (the job runs, or the helper has not reported yet): the page waits for it.</summary>
    public bool Restarting => Running is not null || Settings.Pending is not null;

    /// <summary>The last automatic run, for its log.</summary>
    public Job? LastAutomatic { get; private set; }

    public string? HelperLog { get; private set; }

    public DateTime NextWindow => UpdateWindow.NextStart(Settings, DateTime.Now);

    /// <summary>The updates of this computer, as MatDock answered just now (or the last answer when it did not).</summary>
    public HostUpdateState Host { get; private set; } = new();

    /// <summary>A check or an install of this computer that waits or runs (its log is the job's), or null.</summary>
    public Job? HostJob { get; private set; }

    /// <summary>Updates are installed on this computer: by a job of this MatOS, or one whose end nobody saw yet (MatOS restarted with Docker).</summary>
    public bool HostInstalling => HostJob?.Title == HostUpdateService.InstallJobTitle || Host.Installing(DateTime.UtcNow);

    /// <summary>The computer restarts: the page waits for it (data-await-restart).</summary>
    public bool HostRestarting => Host.Restarting(DateTime.UtcNow);

    /// <summary>The last install of this computer that ended, for its log.</summary>
    public Job? LastHostInstall { get; private set; }

    public async Task<IActionResult> OnPostHostCheckAsync()
    {
        await LoadAsync();
        if (HostJob is null && !HostRestarting)
        {
            await _jobs.EnqueueAsync(JobKind.Update, HostUpdateService.CheckJobTitle, null, HostUpdateService.CheckJob(), HttpContext.RequestAborted);
        }

        return RedirectToPage(null, null, "upd-host");
    }

    public async Task<IActionResult> OnPostHostInstallAsync()
    {
        await LoadAsync();
        if (HostJob is null && !HostInstalling && !HostRestarting && Host.Manageable)
        {
            await _jobs.EnqueueAsync(JobKind.Update, HostUpdateService.InstallJobTitle, null, HostUpdateService.InstallJob(), HttpContext.RequestAborted);
        }

        return RedirectToPage(null, null, "upd-host");
    }

    public async Task<IActionResult> OnPostHostRestartAsync()
    {
        try
        {
            await _host.RestartAsync(HttpContext.RequestAborted);
            StatusMessage = "This computer restarts. MatOS and your apps are away for a few minutes and come back by themselves.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"This computer was not restarted: {ex.Message}";
            TempData["IsError"] = true;
        }

        return RedirectToPage(null, null, "upd-host");
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Input = new InputModel
        {
            Mode = Settings.Chosen ? Settings.EffectiveMode : UpdateMode.Recommended,
            WindowStart = Settings.WindowStart.ToString("HH:mm"),
            BackupFirst = Settings.BackupFirst,
            HostUpdates = Settings.HostUpdates,
            HostRestart = Settings.HostRestart,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var mode = UpdateMode.Normalize(Input.Mode);
        if (mode is null)
        {
            ModelState.AddModelError("Input.Mode", "Please choose what is updated automatically.");
        }

        // Off hides the time and the backup (they post nothing): they keep what they were.
        TimeOnly? start = null;
        if (mode is not null && mode != UpdateMode.Off)
        {
            if (TimeOnly.TryParseExact(Input.WindowStart?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], out var parsed))
            {
                start = parsed;
            }
            else
            {
                ModelState.AddModelError("Input.WindowStart", "Please enter a time like 03:00.");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        await _config.UpdateAsync(c =>
        {
            c.Updates.Mode = mode;
            if (start is { } time)
            {
                c.Updates.WindowStartMinute = time.Hour * 60 + time.Minute;
                c.Updates.BackupFirst = Input.BackupFirst;
                c.Updates.HostUpdates = Input.HostUpdates;
                c.Updates.HostRestart = Input.HostUpdates && Input.HostRestart;
            }
        }, HttpContext.RequestAborted);
        StatusMessage = "Updates saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var check = await _self.CheckAsync(HttpContext.RequestAborted);
        StatusMessage = check.Error is { } error ? $"MatOS could not check for a newer version: {error}"
            : check.Available ? "A newer MatOS is there." : "MatOS is up to date.";
        TempData["IsError"] = check.Error is not null;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        await LoadAsync();
        if (!Restarting)
        {
            await _jobs.EnqueueAsync(JobKind.Update, AutoUpdate.SelfJobTitle, null, AutoUpdate.Self(), HttpContext.RequestAborted);
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        await _self.TakeOverResultAsync(ct);
        Settings = (await _config.GetAsync(ct)).Updates;
        Running = await _db.Jobs.AsNoTracking()
            .Where(j => j.Title == AutoUpdate.SelfJobTitle && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        LastAutomatic = await _db.Jobs.AsNoTracking().Where(j => j.Title == AutoUpdate.JobTitle).OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        HelperLog = Settings.Last is not null ? _self.ReadHelperLog() : null;
        Host = await _host.ReadAsync(ct);
        HostJob = await _db.Jobs.AsNoTracking()
            .Where(j => (j.Title == HostUpdateService.InstallJobTitle || j.Title == HostUpdateService.CheckJobTitle)
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        LastHostInstall = HostJob is not null ? null
            : await _db.Jobs.AsNoTracking().Where(j => j.Title == HostUpdateService.InstallJobTitle).OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
    }
}
