using Docker.DotNet.Models;
using MatOS.Web.Docker;
using MatOS.Web.Services;

namespace MatOS.Web.Engine;

/// <summary>Port pool + per-app instance counter. Persisted as store.json.</summary>
public class InstallStore
{
    public int NextPort { get; set; } = 20000;
    public Dictionary<string, int> Counters { get; set; } = new();
}

public record InstallResult(bool Ok, string? Name, int HostPort, string? Error);

/// <summary>
/// Installs a catalog app as a container: allocates a host port from the pool, a per-install
/// data volume, stamps matOS + Matcad labels, joins the shared network, and starts it. Supports
/// multiple installs of the same app (unique name/volume/port per instance).
/// </summary>
public class InstallService
{
    private readonly DockerService _docker;
    private readonly JsonConfigService _config;
    private readonly StoreService _store;
    private readonly ILogger<InstallService> _log;
    private readonly string _network;
    private readonly object _gate = new();

    public InstallService(DockerService docker, JsonConfigService config, StoreService store, IConfiguration cfg, ILogger<InstallService> log)
    {
        _docker = docker; _config = config; _store = store; _log = log;
        _network = cfg["MatOS:Docker:Network"] ?? "matos";
    }

    public async Task<InstallResult> InstallAsync(string appId, CancellationToken ct = default)
    {
        var app = _store.Find(appId);
        if (app == null) return new(false, null, 0, "Unknown app.");

        int instance;
        lock (_gate)
        {
            var store = _config.Get<InstallStore>("store");
            store.Counters.TryGetValue(appId, out var c);
            instance = c + 1; store.Counters[appId] = instance;
        }

        // pick a free host port (pool high-water, skipping ports already published)
        var used = (await _docker.ListContainersAsync(true, ct)).SelectMany(c => c.Ports)
            .Where(p => p.PublicPort is > 0).Select(p => p.PublicPort!.Value).ToHashSet();
        int port;
        lock (_gate)
        {
            var store = _config.Get<InstallStore>("store");
            port = Math.Max(20000, store.NextPort);
            while (used.Contains(port)) port++;
            store.NextPort = port + 1;
        }
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));

        var name = $"matos_{appId}_{instance}";
        try
        {
            await _docker.EnsureNetworkAsync(_network, ct);
            await _docker.PullImageBestEffortAsync(app.Image, ct);

            var exposed = new Dictionary<string, EmptyStruct> { [$"{app.UiPort}/tcp"] = default };
            var bindings = new Dictionary<string, IList<PortBinding>>
            {
                [$"{app.UiPort}/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } }
            };
            var mounts = new List<Mount>();
            for (int i = 0; i < app.Volumes.Length; i++)
                mounts.Add(new Mount { Type = "volume", Source = $"{name}_data{i}", Target = app.Volumes[i] });

            var labels = new Dictionary<string, string>
            {
                ["matos.managed"] = "true",
                ["matos.app"] = appId,
                ["matos.instance"] = instance.ToString(),
                ["matos.title"] = app.Name,
                // Matcad picks these up (auto-names <container>.<BaseDomain> when discovery is enabled).
                ["matcad.enable"] = "true",
                ["matcad.port"] = app.UiPort.ToString(),
            };
            var env = app.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList();

            var p = new CreateContainerParameters
            {
                Image = app.Image, Name = name, Env = env, Labels = labels, ExposedPorts = exposed,
                HostConfig = new HostConfig
                {
                    PortBindings = bindings,
                    Mounts = mounts,
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    NetworkMode = _network
                }
            };
            await _docker.CreateAndStartAsync(p, ct);
            _log.LogInformation("Installed {App} as {Name} on host port {Port}", appId, name, port);
            return new(true, name, port, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Install of {App} failed", appId);
            return new(false, name, port, ex.Message);
        }
    }

    public async Task<InstallResult> UninstallAsync(string id, bool removeVolume, CancellationToken ct = default)
    {
        try
        {
            var detail = await _docker.InspectDetailAsync(id, ct);
            await _docker.RemoveContainerAsync(id, force: true, ct);
            if (removeVolume && detail != null)
                foreach (var m in detail.Mounts.Where(m => m.Type == "volume" && !string.IsNullOrEmpty(m.Name)))
                    await _docker.RemoveVolumeAsync(m.Name!, ct);
            return new(true, null, 0, null);
        }
        catch (Exception ex) { return new(false, null, 0, ex.Message); }
    }
}
