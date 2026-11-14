using Mattiverse.Core.Docker;
using Mattiverse.Core.Engines;
using Mattiverse.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Tasks;

/// <summary>
/// The Task Manager: every container of the host with what it uses right now, and the load of the host. All of it is MatDock's answer
/// (<see cref="RuntimeReader"/>), and starting, stopping and restarting a container is MatDock's doing too: Mattiverse only asks.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 25;

    private static readonly string[] Actions = ["start", "stop", "restart"];

    private readonly RuntimeReader _runtime;
    private readonly MatDockClient _matDock;
    private readonly DockerService _docker;

    public IndexModel(RuntimeReader runtime, MatDockClient matDock, DockerService docker)
    {
        _runtime = runtime;
        _matDock = matDock;
        _docker = docker;
    }

    public ListView<ContainerTask> List { get; private set; } = default!;

    /// <summary>The host: its load is known when MatDock measured it.</summary>
    public DockerEngineInfo Engine { get; private set; } = default!;

    /// <summary>MatDock answered: the list is the whole host and has the usage. Without it only the own containers are listed, and nothing can be done to them.</summary>
    public bool Measured { get; private set; }

    public int Total { get; private set; }

    public int Running { get; private set; }

    /// <summary>The container Mattiverse itself runs in: it does not stop itself.</summary>
    public string? Self { get; private set; }

    /// <summary>A container of this Mattiverse can be restarted and stopped here, except Mattiverse itself and MatDock (the one that would do it).</summary>
    public bool CanAct(ContainerTask task) => Measured && task.Own && task.Container.Name != Self && task.Container.Instance != EngineIds.MatDock;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var q = ListQuery.Text(Request, "q");
        var part = ListQuery.Text(Request, "part") is "own" or "other" ? ListQuery.Text(Request, "part") : null;
        var state = ListQuery.Text(Request, "state") is "running" or "stopped" ? ListQuery.Text(Request, "state") : null;
        var sort = ListQuery.Text(Request, "sort") is "memory" or "name" ? ListQuery.Text(Request, "sort")! : "cpu";
        var descending = ListQuery.Descending(Request, defaultDescending: true);

        var (tasks, measured) = await _runtime.ListTasksAsync(ct);
        Measured = measured;
        Engine = await _runtime.GetEngineInfoAsync(ct);
        Self = await _docker.GetSelfContainerNameAsync(ct);
        Total = tasks.Count;
        Running = tasks.Count(t => t.Container.IsRunning);

        IEnumerable<ContainerTask> query = tasks;
        if (part is not null)
        {
            query = query.Where(t => t.Own == (part == "own"));
        }

        if (state is not null)
        {
            query = query.Where(t => t.Container.IsRunning == (state == "running"));
        }

        if (q is not null)
        {
            query = query.Where(t => Has(t.Container.Name, q) || Has(t.Container.Project, q) || Has(t.Container.Image, q));
        }

        // A container nobody measured (a stopped one) is always below the measured ones, whichever way the list is turned.
        query = sort switch
        {
            "name" => descending ? query.OrderByDescending(t => t.Container.Name, StringComparer.OrdinalIgnoreCase) : query.OrderBy(t => t.Container.Name, StringComparer.OrdinalIgnoreCase),
            "memory" => Ordered(query, t => t.MemoryBytes, descending),
            _ => Ordered(query, t => t.CpuPercent, descending),
        };

        var matching = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "tasks",
            Action = "/Tasks",
            Label = "Task Manager",
            Search = q,
            SearchPlaceholder = "Search containers",
            SearchField = true,
            Filters =
            [
                new FilterField("part", "Part of", [new("own", "Mattiverse"), new("other", "Something else on this host")], part),
                new FilterField("state", "State", [new("running", "Running"), new("stopped", "Stopped")], state),
            ],
            SortOptions = [new("cpu", "Processor"), new("memory", "Memory"), new("name", "Name")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "cpu",
            DefaultDescending = true,
            Total = Total,
            Matching = matching.Count,
            Noun = Total == 1 ? "container" : "containers",
        };

        var pages = Math.Max(1, (int)Math.Ceiling(matching.Count / (double)PageSize));
        var page = Math.Clamp(ListQuery.Page(Request), 1, pages);
        List = new ListView<ContainerTask>
        {
            Items = matching.Skip((page - 1) * PageSize).Take(PageSize).ToList(),
            Toolbar = toolbar,
            Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = toolbar.UrlWithPage },
        };
    }

    /// <summary>Starts, stops or restarts a container of this Mattiverse: MatDock does it.</summary>
    public async Task<IActionResult> OnPostActionAsync(string name, string verb)
    {
        var ct = HttpContext.RequestAborted;
        var (tasks, measured) = await _runtime.ListTasksAsync(ct);
        Measured = measured;
        Self = await _docker.GetSelfContainerNameAsync(ct);
        var task = tasks.FirstOrDefault(t => t.Container.Name == name);
        if (task is null || !Actions.Contains(verb) || !CanAct(task))
        {
            TempData["StatusMessage"] = measured ? "This container cannot be changed here." : "MatDock does not answer, so nothing can be changed right now.";
            TempData["IsError"] = true;
            return Redirect("/Tasks");
        }

        try
        {
            await _matDock.ContainerActionAsync(name, verb, ct);
            TempData["StatusMessage"] = verb switch { "start" => $"{name} is started.", "stop" => $"{name} is stopped.", _ => $"{name} is restarted." };
        }
        catch (InvalidOperationException ex)
        {
            TempData["StatusMessage"] = ex.Message;
            TempData["IsError"] = true;
        }

        return Redirect("/Tasks");
    }

    private static bool Has(string text, string q) => text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ContainerTask> Ordered<T>(IEnumerable<ContainerTask> query, Func<ContainerTask, T?> key, bool descending)
        where T : struct
        => (descending
                ? query.OrderBy(t => key(t) is null).ThenByDescending(t => key(t))
                : query.OrderBy(t => key(t) is null).ThenBy(t => key(t)))
            .ThenBy(t => t.Container.Name, StringComparer.OrdinalIgnoreCase);
}
