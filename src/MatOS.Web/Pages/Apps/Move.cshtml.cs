using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Core.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

/// <summary>
/// Moves an app to another node (docs/ARCHITECTURE.md "Nodes"): the node to go to, what happens, then a job (<see cref="AppService.RunMoveAsync"/>).
/// </summary>
public class MoveModel : PageModel
{
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly NodeService _nodes;

    public MoveModel(AppService apps, JobService jobs, NodeService nodes)
    {
        _apps = apps;
        _jobs = jobs;
        _nodes = nodes;
    }

    public InstalledApp App { get; private set; } = null!;

    /// <summary>Where the app runs now.</summary>
    public string Here { get; private set; } = NodeService.ThisMachine;

    /// <summary>The nodes it can go to (every other one that is switched on).</summary>
    public IReadOnlyList<NodeInfo> Targets { get; private set; } = [];

    /// <summary>Why the app cannot move, or null.</summary>
    public string? Refused { get; private set; }

    /// <summary>The node to go to: its id, or <c>0</c> for this machine.</summary>
    [BindProperty]
    public long? To { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var target = To is null ? null : Targets.FirstOrDefault(n => To == 0 ? n.IsLocal : n.Id == To && !n.IsLocal);
        if (Refused is not null || target is null)
        {
            ModelState.AddModelError(string.Empty, Refused ?? "Choose the node to move the app to.");
            return Page();
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Move, $"Move {App.Name} to {target.DisplayName}", App, AppJobs.Move(App.Id, target.AppNodeId), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync(long id)
    {
        var ct = HttpContext.RequestAborted;
        if (await _apps.GetAsync(id, ct) is not { } app)
        {
            return false;
        }

        App = app;
        Here = await _nodes.NameOfAsync(app.NodeId, ct);
        Targets = (await _nodes.ChoicesAsync(ct)).Where(n => n.AppNodeId != app.NodeId).ToList();
        Refused = AppService.CannotMove(app)
            ?? (Targets.Count == 0 ? "There is no other node to move it to. Add one under Settings, Nodes." : null);
        return true;
    }
}
