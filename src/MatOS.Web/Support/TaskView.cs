using MatOS.Core.Tasks;
using MatOS.Web.Controls;

namespace MatOS.Web.Support;

/// <summary>A card of the host on top of the Task Manager: its number, a line under it, and a small chart (or a meter).</summary>
public sealed record TaskCard(string Key, string Label, string Icon, string Value, string Sub, string Line, string Area, double? Meter = null, string? Empty = null)
{
    public bool HasChart => Line.Length > 0;
}

/// <summary>
/// What the Task Manager shows for one request (the page and <c>GET /api/tasks</c>, which js/tasks.js polls): the cards of the host, the groups of the page
/// the toolbar chose, the toolbar and the pager. The numbers are written here, so the page and its JSON say the same; the script only puts them in place.
/// </summary>
public sealed class TaskView
{
    public const int PageSize = 50;

    public required TaskSnapshot Snapshot { get; init; }

    public required TaskQuery Query { get; init; }

    public required ListView<TaskGroup> List { get; init; }

    public required IReadOnlyList<TaskCard> Cards { get; init; }

    public IEnumerable<(TaskSection Section, List<TaskGroup> Groups)> Sections
        => List.Items.GroupBy(g => g.Section).OrderBy(g => g.Key).Select(g => (g.Key, g.ToList()));

    /// <summary>What changes the rows themselves (which there are, running or not, what can be done): when it differs, the script fetches the page again.</summary>
    public string Signature => string.Join('|', List.Items.Select(g =>
        $"{g.Key}:{(int)g.Actions}:{string.Join(',', g.Rows.Select(r => $"{r.Key}{(r.IsRunning ? '+' : '-')}{(int)r.Actions}"))}"));

    /// <summary>The line under the cards: how long the host is up, what runs, the version of Docker.</summary>
    public string Facts
    {
        get
        {
            var parts = new List<string>();
            if (Snapshot.Host?.UptimeSeconds is { } up)
            {
                parts.Add("Up for " + TaskFormat.Uptime(up));
            }

            parts.Add($"{Snapshot.Running} of {Snapshot.Total} {(Snapshot.Total == 1 ? "container" : "containers")} running");
            if (!string.IsNullOrEmpty(Snapshot.Host?.DockerVersion))
            {
                parts.Add("Docker " + Snapshot.Host.DockerVersion);
            }

            return string.Join(" · ", parts);
        }
    }

    public static string SectionLabel(TaskSection section) => section switch
    {
        TaskSection.Apps => "Apps",
        TaskSection.System => "System",
        _ => "Something else on this host",
    };

    /// <summary>"running", "partial" or "stopped".</summary>
    public static string StateOf(TaskGroup group)
        => group.Running == group.Total ? "running" : group.Running == 0 ? "stopped" : "partial";

    public static string StateText(TaskGroup group)
        => group.Running == group.Total
            ? group.Total == 1 ? "Running" : $"{group.Total} containers running"
            : group.Running == 0 ? "Stopped" : $"{group.Running} of {group.Total} running";

    public static string RowSub(TaskRow row)
        => string.IsNullOrEmpty(row.Container.Service) ? row.Container.Image : $"{row.Container.Service} · {row.Container.Image}";

    /// <summary>Where the app of a group opens in a window: an app at its page, an engine at its overview; nothing for MatOS and the rest of the host.</summary>
    public static string? OpenUrl(TaskGroup group)
        => group.App is not { } app ? null : app.IsEngine ? "/Engines?id=" + Uri.EscapeDataString(app.CatalogId) : "/Apps/Open?id=" + app.Id;

    public static string LogUrl(TaskRow row) => "/Tasks/Log?name=" + Uri.EscapeDataString(row.Container.Name);

    public static string MemorySub(double? percent) => percent is null ? string.Empty : TaskFormat.Percent(percent) + " of the host";

    public static TaskView From(HttpRequest request, TaskSnapshot snapshot)
    {
        var part = ListQuery.Text(request, "part") is { } p && TaskQuery.Parts.Contains(p) ? p : null;
        var state = ListQuery.Text(request, "state") is "running" or "stopped" ? ListQuery.Text(request, "state") : null;
        var sort = ListQuery.Text(request, "sort") is { } s && TaskQuery.Sorts.Contains(s) ? s : "cpu";
        var query = new TaskQuery(ListQuery.Text(request, "q"), part, state, sort, ListQuery.Descending(request, defaultDescending: sort != "name"));

        var matching = TaskBoard.Apply(snapshot.Groups, query);
        var toolbar = new ToolbarModel
        {
            Id = "tasks",
            Action = "/Tasks",
            Label = "Task Manager",
            Search = query.Search,
            SearchPlaceholder = "Search apps and containers",
            SearchField = true,
            Filters =
            [
                new FilterField("part", "Part", [new("apps", "Apps"), new("system", "System"), new("other", "Something else on this host")], part),
                new FilterField("state", "State", [new("running", "Running"), new("stopped", "Stopped")], state),
            ],
            SortOptions = [new("cpu", "Processor"), new("memory", "Memory"), new("network", "Network"), new("disk", "Disk"), new("name", "Name")],
            Sort = sort,
            Descending = query.Descending,
            DefaultSort = "cpu",
            DefaultDescending = true,
            Total = snapshot.Groups.Count,
            Matching = matching.Count,
            Noun = snapshot.Groups.Count == 1 ? "app or program" : "apps and programs",
        };
        var (items, pager) = ListQuery.Slice(matching, request, PageSize, toolbar.UrlWithPage);
        return new TaskView
        {
            Snapshot = snapshot,
            Query = query,
            List = new ListView<TaskGroup> { Items = items, Toolbar = toolbar, Pager = pager },
            Cards = CardsOf(snapshot),
        };
    }

    /// <summary>The address of the list sorted by <paramref name="sort"/>: the other way round when it is sorted by it already.</summary>
    public string SortUrl(string sort)
    {
        var descending = Query.Sort == sort ? !Query.Descending : sort != "name";
        var toolbar = List.Toolbar;
        var others = toolbar.UrlWithout("sort");
        var join = others.Contains('?') ? "&" : "?";
        return sort == "cpu" && descending ? others : $"{others}{join}sort={sort}&dir={(descending ? "desc" : "asc")}";
    }

    /// <summary><c>ascending</c>, <c>descending</c> or <c>none</c> for a column (aria-sort).</summary>
    public string AriaSort(string sort) => Query.Sort != sort ? "none" : Query.Descending ? "descending" : "ascending";

    private static List<TaskCard> CardsOf(TaskSnapshot snapshot)
    {
        var host = snapshot.Host;
        var points = snapshot.Points;
        var lastNet = points.LastOrDefault(p => p.Net is not null)?.Net;
        var lastDisk = points.LastOrDefault(p => p.Disk is not null)?.Disk;
        var (cpuLine, cpuArea) = TaskFormat.Chart(points.Select(p => p.Cpu).ToList(), 100);
        var (memLine, memArea) = TaskFormat.Chart(points.Select(p => p.Memory).ToList(), 100);
        var (netLine, netArea) = TaskFormat.Chart(points.Select(p => p.Net).ToList(), floor: 64 * 1024);
        var (diskLine, diskArea) = TaskFormat.Chart(points.Select(p => p.Disk).ToList(), floor: 256 * 1024);
        const string Gathering = "The chart fills while this page is open.";
        var noIo = snapshot.Measured && !snapshot.HasIo ? "A newer MatDock tells this." : Gathering;

        return
        [
            new("cpu", "Processor", "activity", TaskFormat.Percent(host?.CpuPercent),
                host?.Cores is { } cores ? $"{cores} {(cores == 1 ? "core" : "cores")}" : "of the whole host", cpuLine, cpuArea, Empty: Gathering),
            new("memory", "Memory", "layers", TaskFormat.Percent(host?.MemoryPercent),
                host is { MemoryUsed: { } used, MemoryTotal: { } total } ? $"{TaskFormat.Size(used)} of {TaskFormat.Size(total)}" : "in use", memLine, memArea, Empty: Gathering),
            new("network", "Network", "network", TaskFormat.Rate(lastNet), "received and sent by the containers", netLine, netArea, Empty: noIo),
            new("disk", "Disk activity", "hard-drive", TaskFormat.Rate(lastDisk), "read and written by the containers", diskLine, diskArea, Empty: noIo),
            new("storage", "Storage", "database", TaskFormat.Percent(host?.DiskPercent),
                host is { DiskUsed: { } dused, DiskTotal: { } dtotal } ? $"{TaskFormat.Size(dused)} of {TaskFormat.Size(dtotal)} used" : "of Docker's disk used",
                string.Empty, string.Empty, Meter: host?.DiskPercent),
        ];
    }

    /// <summary>What js/tasks.js puts in place: the cards, the facts, the signature of the rows and the numbers of every group and container.</summary>
    public object ToJson() => new
    {
        measured = Snapshot.Measured,
        error = Snapshot.Error,
        facts = Facts,
        signature = Signature,
        cards = Cards.Select(c => new { key = c.Key, value = c.Value, sub = c.Sub, line = c.Line, area = c.Area, meter = c.Meter, tone = TaskFormat.Tone(c.Meter) }),
        sections = Sections.Select(s => new { key = s.Section.ToString().ToLowerInvariant(), groups = s.Groups.Select(g => g.Key) }),
        groups = List.Items.Select(g => new
        {
            key = g.Key,
            state = StateOf(g),
            stateText = StateText(g),
            cpu = TaskFormat.Percent(g.Cpu),
            cpuMeter = g.Cpu,
            cpuTone = TaskFormat.Tone(g.Cpu),
            memory = TaskFormat.Size(g.MemoryBytes),
            memorySub = MemorySub(g.MemoryPercent),
            net = TaskFormat.Rate(g.Net),
            disk = TaskFormat.Rate(g.Disk),
            rows = g.Rows.Select(r => new
            {
                key = r.Key,
                cpu = TaskFormat.Percent(r.Task.CpuPercent),
                cpuMeter = r.Task.CpuPercent,
                cpuTone = TaskFormat.Tone(r.Task.CpuPercent),
                memory = TaskFormat.Size(r.Task.MemoryBytes),
                memorySub = MemorySub(r.Task.MemoryPercent),
                net = TaskFormat.Rate(r.Rate.Net),
                disk = TaskFormat.Rate(r.Rate.Disk),
                status = MatOS.Core.Docker.ContainerStatusText.Plain(r.Container.Status),
            }),
        }),
    };
}
