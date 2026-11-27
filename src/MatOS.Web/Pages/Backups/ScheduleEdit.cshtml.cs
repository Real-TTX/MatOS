using System.ComponentModel.DataAnnotations;
using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Backups;

public class ScheduleEditModel : PageModel
{
    private readonly BackupHub _backups;
    private readonly AppService _apps;
    private readonly SystemConfigService _config;
    private readonly BackupTargetService _targets;

    public ScheduleEditModel(BackupHub backups, AppService apps, SystemConfigService config, BackupTargetService targets)
    {
        _backups = backups;
        _apps = apps;
        _config = config;
        _targets = targets;
    }

    /// <summary>The targets the backups can be kept at besides this machine (empty: the choice is not shown).</summary>
    public List<BackupTarget> Targets { get; private set; } = new();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<InstalledApp> Apps { get; private set; } = new();

    /// <summary>The engine that keeps an existing schedule, for the label on the form.</summary>
    public string? Engine { get; private set; }

    public bool IsEdit => !string.IsNullOrEmpty(Input.Id);

    [TempData] public string? StatusMessage { get; set; }

    public class InputModel
    {
        /// <summary><c>&lt;engine&gt;:&lt;id&gt;</c> of an existing schedule; empty for a new one.</summary>
        public string? Id { get; set; }

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(200)]
        public string Name { get; set; } = "Nightly backup";

        /// <summary>The slug of the app to back up, or empty for all apps.</summary>
        public string? AppSlug { get; set; }

        public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

        [Required(ErrorMessage = "Please choose a time.")]
        public string Time { get; set; } = "03:00";

        public DayOfWeek Weekday { get; set; } = DayOfWeek.Sunday;

        [Range(1, 365, ErrorMessage = "Keep between 1 and 365 backups.")]
        public int Retention { get; set; } = 7;

        public bool StopContainers { get; set; } = true;

        public bool Enabled { get; set; } = true;

        /// <summary>Where the backups are kept: "0" this machine, else a target's id.</summary>
        public string? Target { get; set; } = "0";
    }

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        await LoadAppsAsync();
        if (!string.IsNullOrEmpty(id))
        {
            var schedule = await _backups.GetScheduleAsync(id, HttpContext.RequestAborted);
            if (schedule is null)
            {
                return NotFound();
            }

            Engine = schedule.Engine;
            Input = new InputModel
            {
                Id = schedule.Id,
                Name = schedule.Name,
                AppSlug = schedule.AppSlug,
                Frequency = schedule.Frequency,
                Time = TimeSpan.FromMinutes(schedule.MinuteOfDay).ToString(@"hh\:mm"),
                Weekday = schedule.Weekday,
                Retention = schedule.Retention,
                StopContainers = schedule.StopContainers,
                Enabled = schedule.Enabled,
                Target = TargetValue(schedule.TargetId),
            };
        }
        else
        {
            var defaults = (await _config.GetAsync(HttpContext.RequestAborted)).Backups;
            Input.Retention = defaults.Retention;
            Input.StopContainers = defaults.StopContainers;
            Input.Target = TargetValue(Targets.FirstOrDefault(t => t.IsDefault)?.Id);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAppsAsync();
        if (!TimeSpan.TryParse(Input.Time, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            ModelState.AddModelError("Input.Time", "Please enter a time like 03:00.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var appSlug = string.IsNullOrEmpty(Input.AppSlug) ? null : Input.AppSlug;
        var app = Apps.FirstOrDefault(a => a.Slug == appSlug);
        try
        {
            await _backups.SaveScheduleAsync(
                new ScheduleInput
                {
                    Id = Input.Id,
                    Name = Input.Name.Trim(),
                    AppSlug = appSlug,
                    Frequency = Input.Frequency,
                    MinuteOfDay = (int)time.TotalMinutes,
                    Weekday = Input.Weekday,
                    Retention = Input.Retention,
                    StopContainers = Input.StopContainers,
                    Enabled = Input.Enabled,
                    TargetId = BackupDestination.Parse(Input.Target).TargetId,
                },
                app?.Engine,
                HttpContext.RequestAborted);

            // The row "Choose backups" of the list "Getting started" ticks off on its next look.
            HttpContext.RequestServices.GetRequiredService<MatOS.Core.Setup.GettingStartedService>().Forget();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }

        StatusMessage = "The schedule was saved.";
        return RedirectToPage("/Backups/Index", new { tab = "schedules" });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!string.IsNullOrEmpty(Input.Id))
        {
            await _backups.DeleteScheduleAsync(Input.Id, HttpContext.RequestAborted);
            StatusMessage = "The schedule was deleted. Existing backups are kept.";
        }

        return RedirectToPage("/Backups/Index", new { tab = "schedules" });
    }

    private async Task LoadAppsAsync()
    {
        Apps = (await _apps.ListAsync(HttpContext.RequestAborted)).Where(a => a.Lifecycle == AppLifecycle.Installed).ToList();
        Targets = await _targets.ListAsync(HttpContext.RequestAborted);
    }

    private static string TargetValue(long? id) => id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
}
