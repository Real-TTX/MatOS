using System.Text.Json.Nodes;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Docker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mattiverse.Core.Engines;

/// <summary>
/// What Mattiverse shows of the runtime (the containers of the apps, their logs, the host): MatDock is asked, because it owns the stacks and their containers
/// (the owner's rule of 2026-11-14: Mattiverse asks the engine and does nothing itself). Docker is only read directly while there is nobody to ask:
/// before MatDock is installed, and while it does not answer (it is being installed, updated or is down), so the home screen never goes blind.
/// </summary>
public sealed class RuntimeReader
{
    private readonly MatDockClient _matDock;
    private readonly DockerService _docker;
    private readonly MattiverseOptions _options;
    private readonly ILogger<RuntimeReader> _logger;

    public RuntimeReader(MatDockClient matDock, DockerService docker, IOptions<MattiverseOptions> options, ILogger<RuntimeReader> logger)
    {
        _matDock = matDock;
        _docker = docker;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The containers of the apps of this Mattiverse, optionally of one app (its slug).</summary>
    public async Task<List<ContainerInfo>> ListContainersAsync(string? instance = null, CancellationToken ct = default)
    {
        var stacks = await AskAsync(() => _matDock.ListStacksAsync(ct), ct);
        if (stacks is null)
        {
            return await _docker.ListContainersAsync(instance, ct);
        }

        var containers = FromStacks(stacks, _options.ProjectPrefix);
        return instance is null ? containers : containers.Where(c => c.Instance == instance).ToList();
    }

    /// <summary>The containers of the stacks whose name starts with the prefix of this Mattiverse; the rest of the name is the app (<c>mattiverse-matdo</c>: <c>matdo</c>).</summary>
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
    /// Every container of the host with what it uses (the Task Manager). Without MatDock only the containers of this Mattiverse are known, and no usage:
    /// <c>Measured</c> says which of the two it is.
    /// </summary>
    public async Task<(List<ContainerTask> Tasks, bool Measured)> ListTasksAsync(CancellationToken ct = default)
    {
        var containers = await AskAsync(() => _matDock.ListContainersAsync(ct), ct);
        return containers is null
            ? ((await _docker.ListContainersAsync(ct: ct)).Select(c => new ContainerTask(c, true)).ToList(), false)
            : (FromContainers(containers, _options.ProjectPrefix), true);
    }

    /// <summary>The containers of the host as MatDock lists them; those of a stack of this Mattiverse (<c>&lt;prefix&gt;-&lt;app&gt;</c>) are its own.</summary>
    public static List<ContainerTask> FromContainers(JsonArray containers, string prefix)
        => containers.OfType<JsonObject>().Select(c =>
        {
            var stack = Text(c, "stack");
            var own = stack.StartsWith(prefix + "-", StringComparison.Ordinal);
            var info = new ContainerInfo(Text(c, "id"), Text(c, "name"), Text(c, "image"), Text(c, "state"), Text(c, "status"), own ? stack[(prefix.Length + 1)..] : string.Empty, Text(c, "service"), Text(c, "ports"), stack);
            return info.IsRunning
                ? new ContainerTask(info, own, Number(c, "cpuPercent"), Number(c, "memoryPercent"), (long)Number(c, "memoryBytes"))
                : new ContainerTask(info, own);
        }).ToList();

    /// <summary>The last lines of the log of a container.</summary>
    public async Task<string> GetLogsAsync(string container, int tail = 300, CancellationToken ct = default)
        => await AskAsync(() => _matDock.GetContainerLogsAsync(container, tail, ct), ct) ?? await _docker.GetLogsAsync(container, tail, ct);

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

    private static double Number(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
}
