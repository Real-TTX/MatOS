using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Core.Security;
using MatOS.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

/// <summary>
/// Where the data folders of an app are (docs/CATALOG.md "Data folders"): each on the app's own volume or on a volume of MatOS (on this machine or a network share).
/// A change is saved and the app deployed again (a job); the files of the old place stay where they are.
/// </summary>
public class FoldersModel : PageModel
{
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly VolumeService _volumes;
    private readonly JobService _jobs;
    private readonly ISecretProtector _protector;

    public FoldersModel(AppService apps, CatalogService catalog, VolumeService volumes, JobService jobs, ISecretProtector protector)
    {
        _apps = apps;
        _catalog = catalog;
        _volumes = volumes;
        _jobs = jobs;
        _protector = protector;
    }

    public InstalledApp App { get; private set; } = null!;

    public CatalogApp Catalog { get; private set; } = null!;

    /// <summary>The volumes a folder can be put on.</summary>
    public List<VolumeView> Choices { get; private set; } = [];

    /// <summary>What is chosen now, by the key of the folder (empty: the app's own volume).</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = new();

    /// <summary>Volumes can be chosen (MatDock is there with the volumes of its API).</summary>
    public bool Available { get; private set; }

    public List<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var variables = AppVariables.Deserialize(Catalog, App.VariablesJson, _protector);
        Values = Catalog.Folders.ToDictionary(f => f.Key, f => (string?)variables.GetValueOrDefault(f.Key));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        foreach (var folder in Catalog.Folders)
        {
            var chosen = Values.GetValueOrDefault(folder.Key);
            if (!string.IsNullOrEmpty(chosen) && Choices.All(v => v.Name != chosen))
            {
                Errors.Add($"{folder.Name}: the volume {chosen} is not one of MatOS's volumes (any more).");
            }
        }

        if (Errors.Count == 0)
        {
            Errors.AddRange(await _apps.SetFoldersAsync(App.Id, Values, HttpContext.RequestAborted));
        }

        if (Errors.Count > 0)
        {
            foreach (var error in Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return Page();
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Redeploy, $"Data folders of {App.Name}", App, AppJobs.Redeploy(App.Id), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync(long id)
    {
        var ct = HttpContext.RequestAborted;
        if (await _apps.GetAsync(id, ct) is not { } app || _catalog.Get(app.CatalogId) is not { Folders.Count: > 0 } catalog)
        {
            return false;
        }

        App = app;
        Catalog = catalog;
        Available = await _volumes.IsAvailableAsync(ct);
        Choices = Available ? await _volumes.ChoicesAsync(ct) : [];
        return true;
    }
}
