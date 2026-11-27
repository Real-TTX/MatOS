using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Engines;

/// <summary>
/// What MatOS shows of the runtime (the containers of the apps, their logs, the host): MatDock is asked, because it owns the stacks and their containers
/// (the owner's rule of 2026-11-14: MatOS asks the engine and does nothing itself). Docker is only read directly while there is nobody to ask:
/// before MatDock is installed, and while it does not answer (it is being installed, updated or is down), so the home screen never goes blind.
/// </summary>
public sealed class RuntimeReader
{
    private readonly MatDockClient _matDock;
    private readonly DockerService _docker;
    private readonly MatOSOptions _options;
    private readonly ILogger<RuntimeReader> _logger;
    private readonly MatOS.Core.Nodes.NodeService _nodes;

    public RuntimeReader(MatDockClient matDock, DockerService docker, IOptions<MatOSOptions> options, ILogger<RuntimeReader> logger, MatOS.Core.Nodes.NodeService nodes)
    {
        _matDock = matDock;
        _docker = docker;
        _options = options.Value;
        _logger = logger;
        _nodes = nodes;
    }

    /// <summary>
    /// The containers of the apps of this MatOS, optionally of one app (its slug): this machine, and every other node that has apps and that MatDock finds
    /// online (docs/ARCHITECTURE.md "Nodes"; a node that is down is not asked, so its apps show no containers).
    /// </summary>
    public async Task<List<ContainerInfo>> ListContainersAsync(string? instance = null, CancellationToken ct = default)
    {
        var stacks = await AskAsync(() => _matDock.ListStacksAsync(ct), ct);
        if (stacks is null)
        {
            return await _docker.ListContainersAsync(instance, ct);
        }

        var containers = FromStacks(stacks, _options.ProjectPrefix);
        foreach (var node in await _nodes.NodesWithAppsAsync(ct))
        {
            if (await _nodes.IsReachableAsync(node, ct) && await AskAsync(() => _matDock.ListStacksAsync(ct, node), ct) is { } remote)
            {
                containers.AddRange(FromStacks(remote, _options.ProjectPrefix));
            }
        }

        return instance is null ? containers : containers.Where(c => c.Instance == instance).ToList();
    }

    /// <summary>
    /// The containers of one app where it runs, also inside a job (the install waits for them): on this machine Docker directly (as a job always read), on
    /// another node from MatDock (there is no socket of that node here). Empty when MatDock does not answer.
    /// </summary>
    public async Task<List<ContainerInfo>> ListAppContainersAsync(string slug, long? node, CancellationToken ct = default)
    {
        if (node is null)
        {
            return await _docker.ListContainersAsync(slug, ct);
        }

        try
        {
            return FromStacks(await _matDock.ListStacksAsync(ct, node) ?? [], _options.ProjectPrefix).Where(c => c.Instance == slug).ToList();
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "MatDock did not list the containers of node {Node}.", node);
            return [];
        }
    }

    /// <summary>The last lines of the log of a container of an app where it runs (inside a job too).</summary>
    public async Task<string> GetAppLogsAsync(string container, long? node, int tail, CancellationToken ct = default)
        => node is null ? await _docker.GetLogsAsync(container, tail, ct) : await _matDock.GetContainerLogsAsync(container, tail, ct, node) ?? string.Empty;

    /// <summary>The containers of the stacks whose name starts with the prefix of this MatOS; the rest of the name is the app (<c>matos-matdo</c>: <c>matdo</c>).</summary>
    public static List<ContainerInfo> FromStacks(JsonArray stacks, string prefix)
    {
        var containers = new List<ContainerInfo>();
        foreach (var stack in stacks.OfType<JsonObject>())
        {
            var name = Text(stack, "name");
            if (!name.StartsWith(prefix + "-", StringComparison.Ordinal) || stack["containers"] is not JsonArray list)
            {
                continue;
            }

            var instance = name[(prefix.Length + 1)..];
            containers.AddRange(list.OfType<JsonObject>().Select(c => new ContainerInfo(
                Text(c, "id"), Text(c, "name"), Text(c, "image"), Text(c, "state"), Text(c, "status"), instance, Text(c, "service"), Text(c, "ports"), name)));
        }

        return containers;
    }

    /// <summary>
    /// Every container of the host with what it uses (the Task Manager). Without MatDock only the containers of this MatOS are known, and no usage:
    /// <c>Measured</c> says which of the two it is.
    /// </summary>
    public async Task<(List<ContainerTask> Tasks, bool Measured)> ListTasksAsync(CancellationToken ct = default, long? node = null)
    {
        if (node is not null)
        {
            // Another node has no socket here: only MatDock can say what runs there, and nothing while it does not answer or the node is down.
            var remote = await _nodes.IsReachableAsync(node, ct) ? await AskAsync(() => _matDock.ListContainersAsync(ct, 3, node), ct) : null;
            return remote is null ? ([], false) : (FromContainers(remote, _options.ProjectPrefix), true);
        }

        var containers = await AskAsync(() => _matDock.ListContainersAsync(ct, maxAgeSeconds: 3), ct);
        return containers is null
            ? ((await _docker.ListContainersAsync(ct: ct)).Select(c => new ContainerTask(c, true)).ToList(), false)
            : (FromContainers(containers, _options.ProjectPrefix), true);
    }

    /// <summary>The containers of the host as MatDock lists them; those of a stack of this MatOS (<c>&lt;prefix&gt;-&lt;app&gt;</c>) are its own.</summary>
    public static List<ContainerTask> FromContainers(JsonArray containers, string prefix)
        => containers.OfType<JsonObject>().Select(c =>
        {
            var stack = Text(c, "stack");
            var own = stack.StartsWith(prefix + "-", StringComparison.Ordinal);
            var info = new ContainerInfo(Text(c, "id"), Text(c, "name"), Text(c, "image"), Text(c, "state"), Text(c, "status"), own ? stack[(prefix.Length + 1)..] : string.Empty, Text(c, "service"), Text(c, "ports"), stack);
            if (!info.IsRunning)
            {
                return new ContainerTask(info, own);
            }

            // cpuOfHostPercent (MatDock with apiVersion 6) is the share of the host; an older MatDock says cpuPercent only, the share of one core.
            var cpu = Maybe(c, "cpuOfHostPercent") ?? Number(c, "cpuPercent");
            var io = new ContainerIo(Bytes(c, "netRxBytes"), Bytes(c, "netTxBytes"), Bytes(c, "blockReadBytes"), Bytes(c, "blockWriteBytes"));
            return new ContainerTask(info, own, cpu, Number(c, "memoryPercent"), (long)Number(c, "memoryBytes"), io is { Net: null, Disk: null } ? null : io);
        }).ToList();

    /// <summary>
    /// The host as the Task Manager shows it: live from MatDock (<c>/host</c>, apiVersion 6), else the last reading of its environment (percent only, up to
    /// five minutes old), else nothing.
    /// </summary>
    public async Task<HostReading?> GetHostAsync(CancellationToken ct = default, long? node = null)
    {
        if (await AskAsync(() => _matDock.GetHostAsync(ct, node), ct) is { } live && FromHost(live) is { } reading)
        {
            return reading;
        }

        var environment = node is null
            ? await AskAsync(() => _matDock.GetLocalEnvironmentAsync(ct), ct)
            : (await AskAsync(() => _matDock.ListEnvironmentsAsync(ct), ct))?.OfType<JsonObject>().FirstOrDefault(e => e["id"] is JsonValue id && id.TryGetValue<long>(out var value) && value == node);
        var engine = FromEnvironment(environment);
        return engine?.Load is { } load
            ? new HostReading(null, load.CpuPercent, null, null, load.MemoryPercent, null, null, load.DiskPercent, null, engine.ContainersRunning, engine.Version, false)
            : null;
    }

    /// <summary>The answer of MatDock's <c>GET /api/v1/host</c>; <c>null</c> while it has no reading yet.</summary>
    public static HostReading? FromHost(JsonObject host)
    {
        var cpu = Maybe(host, "cpuPercent");
        var memory = Maybe(host, "memoryPercent");
        if (cpu is null && memory is null)
        {
            return null;
        }

        return new HostReading((int?)Maybe(host, "cores"), cpu, Bytes(host, "memoryTotalBytes"), Bytes(host, "memoryUsedBytes"), memory,
            Bytes(host, "diskTotalBytes"), Bytes(host, "diskUsedBytes"), Maybe(host, "diskPercent"), Bytes(host, "uptimeSeconds"),
            (int?)Maybe(host, "containersRunning"), Text(host, "dockerVersion"), true);
    }

    /// <summary>The last lines of the log of a container.</summary>
    public async Task<string> GetLogsAsync(string container, int tail = 300, CancellationToken ct = default, long? node = null)
        => node is not null
            ? await AskAsync(() => _matDock.GetContainerLogsAsync(container, tail, ct, node), ct) ?? "The node does not answer."
            : await AskAsync(() => _matDock.GetContainerLogsAsync(container, tail, ct), ct) ?? await _docker.GetLogsAsync(container, tail, ct);

    /// <summary>The Docker engine of this host and, from MatDock, how busy the host is.</summary>
    public async Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default)
    {
        var local = await AskAsync(() => _matDock.GetLocalEnvironmentAsync(ct), ct);
        return FromEnvironment(local) ?? await _docker.GetEngineInfoAsync(ct);
    }

    /// <summary>The local environment of MatDock as the facts of the engine; <c>null</c> while MatDock has not measured it yet.</summary>
    public static DockerEngineInfo? FromEnvironment(JsonObject? environment)
    {
        if (environment?["reading"] is not JsonObject reading || Text(environment, "status") != "online")
        {
            return null;
        }

        var load = new HostLoad(Number(reading, "cpuPercent"), Number(reading, "memoryPercent"), Number(reading, "diskPercent"));
        return new DockerEngineInfo(true, null, Text(environment, "dockerVersion"), string.Empty, 0, 0, (int)Number(reading, "containersRunning"), 0, 0, load, "MatDock");
    }

    /// <summary>Asks MatDock; <c>null</c> when it is not installed or does not answer, and Docker is read instead.</summary>
    private async Task<T?> AskAsync<T>(Func<Task<T?>> ask, CancellationToken ct)
        where T : class
    {
        // A job waits for an engine that restarts (EngineClient.InJob); a page must not, so only pages ask MatDock here.
        if (EngineClient.InJob.Value || !await _matDock.IsInstalledAsync(ct))
        {
            return null;
        }

        try
        {
            return await ask();
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "MatDock did not answer; reading Docker directly.");
            return null;
        }
    }

    private static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static double Number(JsonObject node, string key) => Maybe(node, key) ?? 0;

    private static double? Maybe(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static long? Bytes(JsonObject node, string key) => Maybe(node, key) is { } number and >= 0 ? (long)number : null;
}
