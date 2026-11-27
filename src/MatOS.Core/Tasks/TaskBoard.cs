using MatOS.Core.Docker;
using MatOS.Core.Engines;

namespace MatOS.Core.Tasks;

/// <summary>Where a group stands in the Task Manager: the apps of MatOS, the system (MatOS itself and its engines), or something else on the host.</summary>
public enum TaskSection
{
    Apps = 0,
    System = 1,
    Other = 2,
}

/// <summary>What can be done to a group or a container from the Task Manager.</summary>
[Flags]
public enum TaskActions
{
    None = 0,
    Restart = 1,

    /// <summary>Stop a running one, start a stopped one.</summary>
    StopStart = 2,
    All = Restart | StopStart,
}

/// <summary>An installed app as the Task Manager names it.</summary>
public sealed record TaskApp(long Id, string Slug, string Name, string? Icon, string CatalogId)
{
    public bool IsEngine => EngineIds.IsEngine(CatalogId);
}

/// <summary>One container in the Task Manager, with what it moves per second.</summary>
public sealed record TaskRow(string Key, ContainerTask Task, TaskRate Rate, TaskActions Actions)
{
    public ContainerInfo Container => Task.Container;

    public bool IsRunning => Task.Container.IsRunning;
}

/// <summary>
/// One line of the Task Manager: an app (its compose project) with its containers, MatOS itself, an engine, or something else on the host (a compose
/// project or a lone container). The sums are over the containers that were measured; <c>null</c> when none was.
/// </summary>
public sealed record TaskGroup(string Key, string Name, TaskSection Section, TaskApp? App, IReadOnlyList<TaskRow> Rows, TaskActions Actions)
{
    public double? Cpu => Sum(Rows.Select(r => r.Task.CpuPercent));

    public long? MemoryBytes => Rows.Any(r => r.Task.MemoryBytes is not null) ? Rows.Sum(r => r.Task.MemoryBytes ?? 0) : null;

    public double? MemoryPercent => Sum(Rows.Select(r => r.Task.MemoryPercent));

    public double? Net => Sum(Rows.Select(r => r.Rate.Net));

    public double? Disk => Sum(Rows.Select(r => r.Rate.Disk));

    public int Running => Rows.Count(r => r.IsRunning);

    public int Total => Rows.Count;

    /// <summary>A lone container (no compose project): the line is the container, there is nothing to expand.</summary>
    public bool IsSingle => Rows.Count == 1 && Key.StartsWith(TaskBoard.ContainerPrefix, StringComparison.Ordinal);

    private static double? Sum(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).ToList();
        return list.Count == 0 ? null : list.Sum(v => v!.Value);
    }
}

/// <summary>The choices of the toolbar: search, part, state and the order.</summary>
public sealed record TaskQuery(string? Search = null, string? Part = null, string? State = null, string Sort = "cpu", bool Descending = true)
{
    public static readonly string[] Sorts = ["cpu", "memory", "network", "disk", "name"];

    public static readonly string[] Parts = ["apps", "system", "other"];
}

/// <summary>
/// The Task Manager's grouping, filter and order (pure, so the tests can hold it): the containers of an app of MatOS are one group (its stack
/// <c>&lt;prefix&gt;-&lt;slug&gt;</c>), MatOS and its engines are the system, and the rest of the host is grouped by compose project.
/// </summary>
public static class TaskBoard
{
    public const string AppPrefix = "app:";
    public const string StackPrefix = "stack:";
    public const string ContainerPrefix = "ctr:";
    public const string RowPrefix = "c:";
    public const string SelfKey = "matos";

    /// <summary>Groups the containers. <paramref name="self"/> is the container MatOS runs in (its compose project is MatOS too); without <paramref name="measured"/> nothing can be done.</summary>
    public static List<TaskGroup> Build(IReadOnlyList<ContainerTask> tasks, IReadOnlyDictionary<string, TaskApp> apps, string? self,
        IReadOnlyDictionary<string, TaskRate> rates, bool measured)
    {
        var selfProject = tasks.FirstOrDefault(t => self is not null && t.Container.Name == self)?.Container.Project;
        var groups = new Dictionary<string, (string Name, TaskSection Section, TaskApp? App, List<ContainerTask> Tasks)>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            var c = task.Container;
            string key;
            string name;
            TaskSection section;
            TaskApp? app = null;
            if (c.Name == self || (!string.IsNullOrEmpty(selfProject) && c.Project == selfProject && !task.Own))
            {
                (key, name, section) = (SelfKey, "MatOS", TaskSection.System);
            }
            else if (task.Own && c.Instance.Length > 0)
            {
                app = apps.GetValueOrDefault(c.Instance);
                key = AppPrefix + c.Instance;
                name = app?.Name ?? c.Instance;
                section = app?.IsEngine == true || EngineIds.IsEngine(c.Instance) ? TaskSection.System : TaskSection.Apps;
            }
            else if (c.Project.Length > 0)
            {
                (key, name, section) = (StackPrefix + c.Project, c.Project, TaskSection.Other);
            }
            else
            {
                (key, name, section) = (ContainerPrefix + c.Name, c.Name, TaskSection.Other);
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = (name, section, app, []);
                groups[key] = group;
            }

            group.Tasks.Add(task);
        }

        return groups.Select(g =>
        {
            var engine = g.Value.Section == TaskSection.System;
            var actions = !measured || g.Key == SelfKey || g.Key == AppPrefix + EngineIds.MatDock || g.Value.Section == TaskSection.Other
                ? TaskActions.None
                : engine ? TaskActions.Restart : TaskActions.All;
            var rows = g.Value.Tasks.Select(t => new TaskRow(RowPrefix + t.Container.Name, t,
                rates.GetValueOrDefault(t.Container.Id) ?? new TaskRate(null, null),
                actions == TaskActions.None || t.Container.Name == self ? TaskActions.None : actions)).ToList();

            // An app group is acted on as an app (its job); a group the toolbar cannot name as an app has no group actions.
            var groupActions = g.Value.App is null ? TaskActions.None : actions;
            return new TaskGroup(g.Key, g.Value.Name, g.Value.Section, g.Value.App, rows, groupActions);
        }).ToList();
    }

    /// <summary>Filters and orders the groups and their containers; a group whose name matches keeps all of its containers.</summary>
    public static List<TaskGroup> Apply(IEnumerable<TaskGroup> groups, TaskQuery query)
    {
        var result = new List<TaskGroup>();
        foreach (var group in groups)
        {
            if (query.Part is { } part && !string.Equals(part, group.Section.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IEnumerable<TaskRow> rows = group.Rows;
            if (query.State is "running" or "stopped")
            {
                rows = rows.Where(r => r.IsRunning == (query.State == "running"));
            }

            if (!string.IsNullOrWhiteSpace(query.Search) && !Has(group.Name, query.Search) && !Has(group.App?.Slug, query.Search))
            {
                rows = rows.Where(r => Has(r.Container.Name, query.Search) || Has(r.Container.Image, query.Search) || Has(r.Container.Service, query.Search));
            }

            var kept = Order(rows, query, r => r.Task.CpuPercent, r => r.Task.MemoryBytes, r => r.Rate.Net, r => r.Rate.Disk, r => r.Container.Name).ToList();
            if (kept.Count > 0)
            {
                result.Add(group with { Rows = kept });
            }
        }

        return Order(result, query, g => g.Cpu, g => g.MemoryBytes, g => g.Net, g => g.Disk, g => g.Name)
            .OrderBy(g => g.Section)
            .ToList();
    }

    private static IEnumerable<T> Order<T>(IEnumerable<T> items, TaskQuery query, Func<T, double?> cpu, Func<T, long?> memory, Func<T, double?> net,
        Func<T, double?> disk, Func<T, string> name)
    {
        // Nothing measured (a stopped container) is always below the measured ones, whichever way the list is turned; equal ones by name.
        Func<T, double?> key = query.Sort switch
        {
            "memory" => t => memory(t),
            "network" => net,
            "disk" => disk,
            _ => cpu,
        };
        if (query.Sort == "name")
        {
            return query.Descending
                ? items.OrderByDescending(name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(name, StringComparer.OrdinalIgnoreCase);
        }

        var sorted = items.OrderBy(t => key(t) is null);
        return (query.Descending ? sorted.ThenByDescending(key) : sorted.ThenBy(key)).ThenBy(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Has(string? text, string q) => text?.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) == true;
}
