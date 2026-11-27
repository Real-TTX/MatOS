using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Tasks;

/// <summary>
/// One look of the Task Manager: the host, the charts and the groups. <see cref="Measured"/>: MatDock answered (the whole host with its usage);
/// otherwise only the containers of MatOS are known, from Docker, without usage. <see cref="Error"/>: neither answered.
/// </summary>
public sealed record TaskSnapshot(
    IReadOnlyList<TaskGroup> Groups,
    HostReading? Host,
    IReadOnlyList<TaskPoint> Points,
    bool Measured,
    string? Error,
    DateTime AtUtc,
    long? Node = null)
{
    public int Total => Groups.Sum(g => g.Total);

    public int Running => Groups.Sum(g => g.Running);

    /// <summary>Network and disk I/O are known (MatDock with apiVersion 6).</summary>
    public bool HasIo => Groups.Any(g => g.Rows.Any(r => r.Task.Io is not null));
}

/// <summary>
/// Reads the Task Manager: what runs from MatDock (<see cref="RuntimeReader"/>), the names and icons of the apps from MatOS, the rates and charts from
/// <see cref="TaskMonitor"/>. It only reads; what the page does to an app or a container goes through a job.
/// </summary>
public sealed class TaskManagerService
{
    private readonly RuntimeReader _runtime;
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly DockerService _docker;
    private readonly TaskMonitors _monitors;
    private readonly ILogger<TaskManagerService> _logger;

    public TaskManagerService(RuntimeReader runtime, AppService apps, CatalogService catalog, DockerService docker, TaskMonitors monitors, ILogger<TaskManagerService> logger)
    {
        _runtime = runtime;
        _apps = apps;
        _catalog = catalog;
        _docker = docker;
        _monitors = monitors;
        _logger = logger;
    }

    /// <summary>One look at this machine or at another node (<paramref name="node"/>, MatDock's environment: docs/ARCHITECTURE.md "Nodes").</summary>
    public async Task<TaskSnapshot> ReadAsync(CancellationToken ct = default, long? node = null)
    {
        var now = DateTime.UtcNow;
        var monitor = _monitors.For(node);
        List<ContainerTask> tasks;
        bool measured;
        try
        {
            (tasks, measured) = await _runtime.ListTasksAsync(ct, node);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception or IOException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Neither MatDock nor Docker answered the Task Manager.");
            return new TaskSnapshot([], null, monitor.Observe([], null, now).Points, false, "Neither MatDock nor Docker answers right now, so nothing can be shown.", now, node);
        }

        if (node is not null && !measured)
        {
            return new TaskSnapshot([], null, monitor.Observe([], null, now).Points, false,
                "This node does not answer right now (it is offline, or MatDock cannot reach it). Test its connection under Settings, Nodes.", now, node);
        }

        var host = measured ? await _runtime.GetHostAsync(ct, node) : null;
        var self = await _docker.GetSelfContainerNameAsync(ct);
        var apps = (await _apps.ListAsync(ct))
            .GroupBy(a => a.Slug, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => { var a = g.First(); return new TaskApp(a.Id, a.Slug, a.Name, _catalog.IconOf(a.CatalogId), a.CatalogId); }, StringComparer.Ordinal);
        var (rates, points) = monitor.Observe(tasks, host, now);
        return new TaskSnapshot(TaskBoard.Build(tasks, apps, node is null ? self : null, rates, measured), host, points, measured, null, now, node);
    }
}
