using System.Text.Json.Nodes;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Tasks;

namespace MatOS.Tests;

/// <summary>The Task Manager: what MatDock says, grouped by app, filtered and ordered, the rates from two readings, and how the numbers are written.</summary>
public class TaskManagerTests
{
    private static ContainerTask Task(string name, string project, bool own, double? cpu = null, long? memory = null, bool running = true, string service = "web",
        string image = "img", ContainerIo? io = null, string? id = null)
    {
        var instance = own && project.StartsWith("matos-", StringComparison.Ordinal) ? project["matos-".Length..] : string.Empty;
        var info = new ContainerInfo(id ?? name, name, image, running ? "running" : "exited", running ? "Up 2 hours" : "Exited (0) 1 hour ago", instance, service, string.Empty, project);
        return running ? new ContainerTask(info, own, cpu ?? 0, 1, memory ?? 0, io) : new ContainerTask(info, own);
    }

    private static readonly Dictionary<string, TaskApp> Apps = new()
    {
        ["matdo"] = new TaskApp(7, "matdo", "MatDo", "matdo.svg", "matdo"),
        ["matdock"] = new TaskApp(1, "matdock", "MatDock", "matdock.svg", "matdock"),
        ["matcad"] = new TaskApp(2, "matcad", "Matcad", "matcad.svg", "matcad"),
    };

    private static List<TaskGroup> Board(bool measured = true) => TaskBoard.Build(
        [
            Task("matos-matdo-web", "matos-matdo", own: true, cpu: 4, memory: 100),
            Task("matos-matdo-db", "matos-matdo", own: true, cpu: 1, memory: 300, service: "db", image: "postgres:17"),
            Task("matos-matdock-matdock", "matos-matdock", own: true, cpu: 2, memory: 50),
            Task("matos-matcad-matcad", "matos-matcad", own: true, cpu: 0.5, memory: 60),
            Task("matos", "matos", own: false, cpu: 3, memory: 200),
            Task("matos-sqlite-web", "matos", own: false, cpu: 0.1, memory: 10),
            Task("nightshift-web-1", "nightshift", own: false, cpu: 9, memory: 20),
            Task("nightshift-db-1", "nightshift", own: false, running: false),
            Task("loner", string.Empty, own: false, cpu: 0.2, memory: 5),
        ],
        Apps, self: "matos", new Dictionary<string, TaskRate>(), measured);

    [Fact]
    public void An_app_is_one_group_of_its_containers_and_MatOS_and_the_engines_are_the_system()
    {
        var groups = Board();

        var matdo = Assert.Single(groups, g => g.Key == "app:matdo");
        Assert.Equal("MatDo", matdo.Name);
        Assert.Equal(TaskSection.Apps, matdo.Section);
        Assert.Equal(2, matdo.Total);
        Assert.Equal(5, matdo.Cpu);
        Assert.Equal(400, matdo.MemoryBytes);
        Assert.Equal(TaskActions.All, matdo.Actions);

        var self = Assert.Single(groups, g => g.Key == TaskBoard.SelfKey);
        Assert.Equal(TaskSection.System, self.Section);
        Assert.Equal(2, self.Total); // MatOS and the rest of its compose project
        Assert.Equal(TaskActions.None, self.Actions);
        Assert.All(self.Rows, r => Assert.Equal(TaskActions.None, r.Actions));

        Assert.Equal(TaskSection.System, groups.Single(g => g.Key == "app:matcad").Section);
        Assert.Equal(TaskActions.Restart, groups.Single(g => g.Key == "app:matcad").Actions);
        Assert.Equal(TaskActions.None, groups.Single(g => g.Key == "app:matdock").Actions); // MatDock does not restart itself from here
    }

    [Fact]
    public void The_rest_of_the_host_is_grouped_by_compose_project_and_only_read()
    {
        var groups = Board();

        var nightshift = Assert.Single(groups, g => g.Key == "stack:nightshift");
        Assert.Equal(TaskSection.Other, nightshift.Section);
        Assert.Equal(1, nightshift.Running);
        Assert.Equal(TaskActions.None, nightshift.Actions);
        Assert.All(nightshift.Rows, r => Assert.Equal(TaskActions.None, r.Actions));
        Assert.False(nightshift.IsSingle);

        var loner = Assert.Single(groups, g => g.Key == "ctr:loner");
        Assert.True(loner.IsSingle);
        Assert.Equal("c:loner", loner.Rows[0].Key);
    }

    [Fact]
    public void Without_MatDock_nothing_can_be_done()
        => Assert.All(Board(measured: false), g => Assert.Equal(TaskActions.None, g.Actions));

    [Fact]
    public void The_list_is_ordered_within_its_part_with_nothing_measured_below()
    {
        var byCpu = TaskBoard.Apply(Board(), new TaskQuery());
        Assert.Equal(["app:matdo", "matos", "app:matdock", "app:matcad", "stack:nightshift", "ctr:loner"], byCpu.Select(g => g.Key));
        Assert.Equal(["nightshift-web-1", "nightshift-db-1"], byCpu.Single(g => g.Key == "stack:nightshift").Rows.Select(r => r.Container.Name));

        var byMemoryUp = TaskBoard.Apply(Board(), new TaskQuery(Sort: "memory", Descending: false));
        Assert.Equal(["app:matdo"], byMemoryUp.Where(g => g.Section == TaskSection.Apps).Select(g => g.Key));
        Assert.Equal(["app:matdock", "app:matcad", "matos"], byMemoryUp.Where(g => g.Section == TaskSection.System).Select(g => g.Key));

        var byName = TaskBoard.Apply(Board(), new TaskQuery(Sort: "name", Descending: false));
        Assert.Equal(["app:matcad", "app:matdock", "matos"], byName.Where(g => g.Section == TaskSection.System).Select(g => g.Key));
    }

    [Fact]
    public void A_search_keeps_a_group_whole_when_its_name_matches_and_the_matching_containers_otherwise()
    {
        var app = TaskBoard.Apply(Board(), new TaskQuery(Search: "matdo"));
        Assert.Contains(app, g => g.Key == "app:matdo" && g.Rows.Count == 2);

        var image = TaskBoard.Apply(Board(), new TaskQuery(Search: "postgres"));
        var only = Assert.Single(image);
        Assert.Equal("matos-matdo-db", Assert.Single(only.Rows).Container.Name);
    }

    [Fact]
    public void The_filters_choose_the_part_and_the_state()
    {
        Assert.All(TaskBoard.Apply(Board(), new TaskQuery(Part: "system")), g => Assert.Equal(TaskSection.System, g.Section));

        var stopped = TaskBoard.Apply(Board(), new TaskQuery(State: "stopped"));
        Assert.Equal("nightshift-db-1", Assert.Single(Assert.Single(stopped).Rows).Container.Name);
    }

    [Fact]
    public void Rates_come_from_two_readings_and_a_shared_sample_keeps_the_last_ones()
    {
        var monitor = new TaskMonitor();
        var t0 = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        List<ContainerTask> Reading(long net, long disk) => [Task("a", "p", own: false, io: new ContainerIo(net, 0, disk, 0), id: "id-a")];
        var host = new HostReading(4, 10, null, null, 50, null, null, null, null, null, null, true);

        var first = monitor.Observe(Reading(1000, 0), host, t0);
        Assert.Null(first.Rates["id-a"].Net);

        var second = monitor.Observe(Reading(4000, 3000), host, t0.AddSeconds(3));
        Assert.Equal(1000, second.Rates["id-a"].Net);
        Assert.Equal(1000, second.Rates["id-a"].Disk);

        // MatDock answered from the same docker stats: nothing moved, the rates of the last step stay, the chart takes no new point
        var same = monitor.Observe(Reading(4000, 3000), host, t0.AddSeconds(4));
        Assert.Equal(1000, same.Rates["id-a"].Net);
        Assert.Equal(2, same.Points.Count);

        // the container restarted: its counters started over
        var restarted = monitor.Observe(Reading(10, 0), host, t0.AddSeconds(7));
        Assert.Null(restarted.Rates["id-a"].Net);
        Assert.Equal(3, restarted.Points.Count);
        Assert.Equal(1000, restarted.Points[1].Net);
        Assert.Equal(10, restarted.Points[1].Cpu);
    }

    [Fact]
    public void The_charts_keep_a_few_minutes()
    {
        var monitor = new TaskMonitor();
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < TaskMonitor.Keep + 15; i++)
        {
            monitor.Observe([], null, t0.AddSeconds(3 * i));
        }

        Assert.Equal(TaskMonitor.Keep, monitor.Observe([], null, t0.AddSeconds(3 * (TaskMonitor.Keep + 20))).Points.Count);
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData(0d, "0 B/s")]
    [InlineData(512d, "512 B/s")]
    [InlineData(12.4 * 1024, "12.4 KB/s")]
    [InlineData(150d * 1024 * 1024, "150 MB/s")]
    public void Rates_are_written_per_second(double? rate, string text) => Assert.Equal(text, TaskFormat.Rate(rate));

    [Theory]
    [InlineData(null, "–")]
    [InlineData(3.94, "3.9 %")]
    [InlineData(42.4, "42 %")]
    public void Percent_has_one_decimal_below_ten(double? value, string text) => Assert.Equal(text, TaskFormat.Percent(value));

    [Theory]
    [InlineData(30L, "1 min")]
    [InlineData(3 * 3600L + 12 * 60, "3 h 12 min")]
    [InlineData(4 * 86400L + 3 * 3600, "4 days 3 h")]
    [InlineData(86400L, "1 day 0 h")]
    public void Uptime_is_short(long seconds, string text) => Assert.Equal(text, TaskFormat.Uptime(seconds));

    [Fact]
    public void A_chart_spans_its_width_with_the_newest_value_at_the_right_and_a_gap_for_a_missing_one()
    {
        Assert.Equal((string.Empty, string.Empty), TaskFormat.Chart([50], 100));

        var (line, area) = TaskFormat.Chart([0, 100], 100);
        Assert.Equal("M0 31L100 1", line);
        Assert.Equal("M0 32L0 31L100 1L100 32Z", area);

        var (last, _) = TaskFormat.Chart([100, 0, 50, 100], 100, slots: 3);
        Assert.Equal("M0 31L50 16L100 1", last);

        var (gap, _) = TaskFormat.Chart([10, 10, null, 20, 20], 100, slots: 5);
        Assert.Equal(2, gap.Count(c => c == 'M'));
    }

    [Fact]
    public void MatDock_says_the_share_of_the_host_and_the_io_of_a_container()
    {
        var containers = (JsonArray)JsonNode.Parse("""
            [
              { "id": "a1", "name": "matos-matdo-web", "image": "matdo", "state": "running", "status": "Up", "stack": "matos-matdo", "service": "web", "ports": "",
                "cpuPercent": 80, "cpuOfHostPercent": 10, "hostCores": 8, "memoryPercent": 1, "memoryBytes": 1024,
                "netRxBytes": 1500, "netTxBytes": 500, "blockReadBytes": 0, "blockWriteBytes": 4096 },
              { "id": "a2", "name": "old", "image": "x", "state": "running", "status": "Up", "stack": "", "service": "", "ports": "", "cpuPercent": 3, "memoryPercent": 1, "memoryBytes": 1 }
            ]
            """)!;

        var tasks = RuntimeReader.FromContainers(containers, "matos");

        Assert.Equal(10, tasks[0].CpuPercent);
        Assert.Equal(2000, tasks[0].Io!.Net);
        Assert.Equal(4096, tasks[0].Io!.Disk);
        Assert.Equal(3, tasks[1].CpuPercent); // an older MatDock: its cpuPercent
        Assert.Null(tasks[1].Io);
    }

    [Fact]
    public void The_live_host_of_MatDock_becomes_the_reading_of_the_host()
    {
        var host = (JsonObject)JsonNode.Parse("""
            { "environmentId": 1, "status": "online", "dockerVersion": "29.1.0", "at": "2026-11-27T12:00:00Z", "cores": 8, "cpuPercent": 12.5, "load1": 1.2,
              "memoryTotalBytes": 16000000000, "memoryUsedBytes": 8000000000, "memoryPercent": 50, "diskTotalBytes": 500000000000, "diskUsedBytes": 50000000000,
              "diskPercent": 10, "uptimeSeconds": 356521, "containersRunning": 59 }
            """)!;

        var reading = RuntimeReader.FromHost(host)!;

        Assert.Equal(8, reading.Cores);
        Assert.Equal(12.5, reading.CpuPercent);
        Assert.Equal(8000000000, reading.MemoryUsed);
        Assert.Equal(356521, reading.UptimeSeconds);
        Assert.Equal("29.1.0", reading.DockerVersion);
        Assert.True(reading.Live);
        Assert.Null(RuntimeReader.FromHost((JsonObject)JsonNode.Parse("""{ "cpuPercent": null, "memoryPercent": null }""")!));
    }
}
