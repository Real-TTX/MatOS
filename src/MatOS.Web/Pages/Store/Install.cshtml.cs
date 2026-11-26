using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Jobs;
using MatOS.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Store;

public class InstallModel : PageModel
{
    private readonly CatalogService _catalog;
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly PortPlanner _ports;

    public InstallModel(CatalogService catalog, AppService apps, JobService jobs, PortPlanner ports)
    {
        _catalog = catalog;
        _apps = apps;
        _jobs = jobs;
        _ports = ports;
    }

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The optional display name (the compose project name is derived from the app, not from this).</summary>
    [BindProperty]
    public string? Name { get; set; }

    /// <summary>The answers to the variable questions, keyed by variable.</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = new();

    public CatalogApp? App { get; private set; }
    public List<string> Errors { get; private set; } = new();
    public List<PortConflict> PortConflicts { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        App = _catalog.Get(Id);
        if (App is null)
        {
            return NotFound();
        }

        await LoadConflictsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        App = _catalog.Get(Id);
        if (App is null)
        {
            return NotFound();
        }

        var result = await _apps.PrepareInstallAsync(new InstallRequest(App.Id, null, Name, Values), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            Errors = result.Errors.ToList();
            await LoadConflictsAsync();
            return Page();
        }

        var app = result.App!;
        var jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task LoadConflictsAsync()
    {
        // The defaults are enough to find the fixed ports (80/443, the mail ports ...).
        var defaults = App!.Variables.Where(v => v.Default is not null).ToDictionary(v => v.Key, v => v.Default!);
        PortConflicts = await _ports.FindConflictsAsync(App, defaults, ownProject: "\0", HttpContext.RequestAborted);
    }
}
