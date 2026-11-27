using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Core.Nodes;
using MatOS.Core.Tasks;
using MatOS.Web.Controls;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Tasks;

/// <summary>
/// The Task Manager: the host (processor, memory, network, disk) with small live charts, and what runs on it grouped by app, with what each uses. All of
/// it is MatDock's answer (<see cref="TaskManagerService"/>); js/tasks.js asks <c>?handler=Live</c> every few seconds and puts the numbers in place.
/// Restarting, stopping and starting is a job that asks MatDock: an app as an app (its stack), a container on its own; only what MatOS runs, never
/// MatOS itself or MatDock, and engines only restart. The folder is for administrators only (Program.cs).
/// </summary>
public class IndexModel : PageModel
{
    private static readonly string[] Verbs = ["start", "stop", "restart"];

    private readonly TaskManagerService _tasks;
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly NodeService _nodes;

    public IndexModel(TaskManagerService tasks, AppService apps, JobService jobs, NodeService nodes)
    {
        _tasks = tasks;
        _apps = apps;
        _jobs = jobs;
        _nodes = nodes;
    }

    public TaskView View { get; private set; } = default!;

    /// <summary>The node the page shows (<c>?node=</c>, MatDock's environment); null is this machine (docs/ARCHITECTURE.md "Nodes").</summary>
    [BindProperty(SupportsGet = true)]
    public long? Node { get; set; }

    /// <summary>A tab per node when there is more than this machine; null otherwise.</summary>
    public TabBarModel? NodeTabs { get; private set; }

    /// <summary>The name of the node the page shows.</summary>
    public string NodeName { get; private set; } = NodeService.ThisMachine;

    /// <summary>Only a node MatDock knows; another number is this machine.</summary>
    private async Task<long?> NodeAsync(CancellationToken ct)
    {
        var nodes = await _nodes.ChoicesAsync(ct);
        var node = Node is { } id ? nodes.FirstOrDefault(n => n.Id == id && !n.IsLocal) : null;
        NodeName = node?.Name ?? NodeService.ThisMachine;
        if (nodes.Count(n => !n.IsLocal) > 0)
        {
            NodeTabs = new TabBarModel
            {
                Tabs = nodes.Select(n => new TabItem
                {
                    Title = n.DisplayName,
                    Href = n.IsLocal ? "/Tasks" : "/Tasks?node=" + n.Id,
                    Active = n.IsLocal ? node is null : n.Id == node?.Id,
                    Badge = n.IsLocal || n.IsOnline ? null : "offline",
                }).ToList(),
            };
        }

        return node?.Id;
    }

    /// <summary>The query of the list, kept through an action (the forms send it back).</summary>
    public string Back => Request.QueryString.HasValue ? Request.QueryString.Value! : string.Empty;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        View = TaskView.From(Request, await _tasks.ReadAsync(ct, await NodeAsync(ct)));
    }

    /// <summary>The numbers of the page for js/tasks.js (the same query as the page).</summary>
    public async Task<IActionResult> OnGetLiveAsync()
    {
        var ct = HttpContext.RequestAborted;
        var view = TaskView.From(Request, await _tasks.ReadAsync(ct, await NodeAsync(ct)));
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(view.ToJson());
    }

    /// <summary>Restarts, stops or starts an app (its whole stack): the job of the app, as on its own page.</summary>
    public async Task<IActionResult> OnPostGroupAsync(string key, string verb, string? back)
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await _tasks.ReadAsync(ct, await NodeAsync(ct));
        var group = snapshot.Groups.FirstOrDefault(g => g.Key == key);
        if (group?.App is not { } target || !Allowed(group.Actions, verb, group.Running > 0) || await _apps.GetAsync(target.Id, ct) is not { } app)
        {
            return Refused(snapshot, back);
        }

        var kind = Kind(verb);
        await _jobs.EnqueueAsync(kind, $"{kind} {app.Name}", app, AppJobs.Lifecycle(app.Id, verb), ct);
        TempData["StatusMessage"] = $"{Doing(verb)} {app.Name}. Activity shows how it goes.";
        return Redirect(BackTo(back));
    }

    /// <summary>Restarts, stops or starts one container of an app: MatDock does it, in a job.</summary>
    public async Task<IActionResult> OnPostContainerAsync(string key, string verb, string? back)
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await _tasks.ReadAsync(ct, await NodeAsync(ct));
        var (group, row) = snapshot.Groups.SelectMany(g => g.Rows.Select(r => (g, r))).FirstOrDefault(p => p.r.Key == key);
        if (row is null || !Allowed(row.Actions, verb, row.IsRunning))
        {
            return Refused(snapshot, back);
        }

        var app = group.App is { } owner ? await _apps.GetAsync(owner.Id, ct) : null;
        var kind = Kind(verb);
        var name = row.Container.Name;
        await _jobs.EnqueueAsync(kind, $"{kind} {name}", app, AppJobs.ContainerAction(name, verb, snapshot.Node), ct);
        TempData["StatusMessage"] = $"{Doing(verb)} {name}. Activity shows how it goes.";
        return Redirect(BackTo(back));
    }

    /// <summary>Restart and stop for what runs, start for what does not; and only what the Task Manager offers for it.</summary>
    private static bool Allowed(TaskActions actions, string verb, bool running)
        => Verbs.Contains(verb) && verb switch
        {
            "restart" => actions.HasFlag(TaskActions.Restart) && running,
            "stop" => actions.HasFlag(TaskActions.StopStart) && running,
            _ => actions.HasFlag(TaskActions.StopStart),
        };

    private IActionResult Refused(TaskSnapshot snapshot, string? back)
    {
        TempData["StatusMessage"] = snapshot.Measured ? "This cannot be changed here." : "MatDock does not answer, so nothing can be changed right now.";
        TempData["IsError"] = true;
        return Redirect(BackTo(back));
    }

    private static JobKind Kind(string verb) => verb switch { "start" => JobKind.Start, "stop" => JobKind.Stop, _ => JobKind.Restart };

    private static string Doing(string verb) => verb switch { "start" => "Starting", "stop" => "Stopping", _ => "Restarting" };

    /// <summary>Back to the list as it was: only a query string of this page, never another address.</summary>
    private static string BackTo(string? back)
        => back is { Length: > 1 } q && q[0] == '?' && !q.Contains('\\') && !q.Contains('\n') && !q.Contains('\r') ? "/Tasks" + q : "/Tasks";
}
