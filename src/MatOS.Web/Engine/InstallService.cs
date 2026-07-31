using System.Diagnostics;
using System.Text;
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
/// Installs a catalog app. "image" apps become a single labelled container; "compose" apps are
/// deployed as a Compose stack via the docker compose CLI (unique project, injected matos/matcad
/// labels + host port, install-variable .env). Both support multiple installs.
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

    public Task<InstallResult> InstallAsync(string appId, IDictionary<string, string>? vars, CancellationToken ct = default)
    {
        var app = _store.Find(appId);
        if (app == null) return Task.FromResult(new InstallResult(false, null, 0, "Unknown app."));
        return string.Equals(app.Kind, "compose", StringComparison.OrdinalIgnoreCase)
            ? InstallComposeAsync(app, vars ?? new Dictionary<string, string>(), ct)
            : InstallImageAsync(app, ct);
    }

    private int NextInstance(string appId)
    {
        lock (_gate) { var s = _config.Get<InstallStore>("store"); s.Counters.TryGetValue(appId, out var c); s.Counters[appId] = c + 1; return c + 1; }
    }

    private async Task<int> NextFreePortAsync(CancellationToken ct)
    {
        var used = (await _docker.ListContainersAsync(true, ct)).SelectMany(c => c.Ports)
            .Where(p => p.PublicPort is > 0).Select(p => p.PublicPort!.Value).ToHashSet();
        int port;
        lock (_gate)
        {
            var s = _config.Get<InstallStore>("store");
            port = Math.Max(20000, s.NextPort);
            while (used.Contains(port)) port++;
            s.NextPort = port + 1;
        }
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        return port;
    }

    // ---- Single image ----
    private async Task<InstallResult> InstallImageAsync(AppDef app, CancellationToken ct)
    {
        var instance = NextInstance(app.Id);
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        var port = await NextFreePortAsync(ct);
        var name = $"matos_{app.Id}_{instance}";
        try
        {
            await _docker.EnsureNetworkAsync(_network, ct);
            await _docker.PullImageBestEffortAsync(app.Image, ct);

            var mounts = new List<Mount>();
            for (int i = 0; i < app.Volumes.Length; i++)
                mounts.Add(new Mount { Type = "volume", Source = $"{name}_data{i}", Target = app.Volumes[i] });

            var p = new CreateContainerParameters
            {
                Image = app.Image, Name = name,
                Env = app.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                Labels = ManagedLabels(app, instance, ui: true),
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{app.UiPort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>> { [$"{app.UiPort}/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } } },
                    Mounts = mounts,
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    NetworkMode = _network
                }
            };
            await _docker.CreateAndStartAsync(p, ct);
            return new(true, name, port, null);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Install (image) of {App} failed", app.Id); return new(false, name, port, ex.Message); }
    }

    private Dictionary<string, string> ManagedLabels(AppDef app, int instance, bool ui) => new()
    {
        ["matos.managed"] = "true",
        ["matos.app"] = app.Id,
        ["matos.instance"] = instance.ToString(),
        ["matos.title"] = app.Name,
        ["matcad.enable"] = ui ? "true" : "false",
        ["matcad.port"] = app.UiPort.ToString(),
    };

    // ---- Compose stack ----
    private async Task<InstallResult> InstallComposeAsync(AppDef app, IDictionary<string, string> vars, CancellationToken ct)
    {
        var services = _store.ParseServices(app.Compose);
        if (services.Count == 0) return new(false, null, 0, "The compose file has no services (or is invalid YAML).");
        var ui = !string.IsNullOrWhiteSpace(app.UiService) && services.Contains(app.UiService) ? app.UiService : services[0];

        var instance = NextInstance(app.Id);
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        var port = await NextFreePortAsync(ct);
        var project = $"matos-{Slug(app.Id)}-{instance}";
        var dir = Path.Combine(Path.GetTempPath(), "matos", project);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "docker-compose.yml"), app.Compose, ct);

        // override: matos/matcad labels on every service, published port on the UI service
        var sb = new StringBuilder();
        sb.AppendLine("services:");
        foreach (var s in services)
        {
            sb.AppendLine($"  \"{s}\":");
            sb.AppendLine("    labels:");
            sb.AppendLine("      matos.managed: \"true\"");
            sb.AppendLine($"      matos.app: \"{app.Id}\"");
            sb.AppendLine($"      matos.instance: \"{instance}\"");
            sb.AppendLine($"      matos.title: \"{YamlStr(app.Name)}\"");
            if (s == ui)
            {
                sb.AppendLine("      matcad.enable: \"true\"");
                sb.AppendLine($"      matcad.port: \"{app.UiPort}\"");
                sb.AppendLine("    ports:");
                sb.AppendLine($"      - \"{port}:{app.UiPort}\"");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "matos-override.yml"), sb.ToString(), ct);

        // install variables -> .env (for ${VAR} substitution) + process env
        var env = new Dictionary<string, string>();
        foreach (var v in app.Variables)
            env[v.Key] = vars.TryGetValue(v.Key, out var val) && !string.IsNullOrEmpty(val) ? val : v.Default;
        var envFile = new StringBuilder();
        foreach (var kv in env) envFile.AppendLine($"{kv.Key}={kv.Value.Replace("\n", " ").Replace("\r", "")}");
        await File.WriteAllTextAsync(Path.Combine(dir, ".env"), envFile.ToString(), ct);

        try { await _docker.EnsureNetworkAsync(_network, ct); } catch { }

        var (code, _, err) = await RunCompose(dir,
            new[] { "-p", project, "-f", "docker-compose.yml", "-f", "matos-override.yml", "up", "-d", "--remove-orphans" }, env, ct);
        if (code != 0) return new(false, project, port, "compose up failed: " + Trim(err));
        _log.LogInformation("Installed compose app {App} as project {Project}", app.Id, project);
        return new(true, project, port, null);
    }

    public async Task<InstallResult> UninstallAsync(string id, bool removeVolume, CancellationToken ct = default)
    {
        try
        {
            var detail = await _docker.InspectDetailAsync(id, ct);
            var project = detail?.ComposeProject;
            if (!string.IsNullOrEmpty(project) && project.StartsWith("matos-", StringComparison.Ordinal))
            {
                var dir = Path.Combine(Path.GetTempPath(), "matos", project);
                var args = new List<string> { "-p", project, "down", "--remove-orphans" };
                if (removeVolume) args.Add("--volumes");
                await RunCompose(Directory.Exists(dir) ? dir : Path.GetTempPath(), args.ToArray(), null, ct);
                return new(true, project, 0, null);
            }
            await _docker.RemoveContainerAsync(id, force: true, ct);
            if (removeVolume && detail != null)
                foreach (var m in detail.Mounts.Where(m => m.Type == "volume" && !string.IsNullOrEmpty(m.Name)))
                    await _docker.RemoveVolumeAsync(m.Name!, ct);
            return new(true, null, 0, null);
        }
        catch (Exception ex) { return new(false, null, 0, ex.Message); }
    }

    // ---- helpers ----
    private static async Task<(int Code, string Out, string Err)> RunCompose(string dir, string[] args, IDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = "docker-compose", WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await o, await e);
    }

    private static string Slug(string s)
    {
        var chars = (s ?? "app").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length > 0 ? slug : "app";
    }
    private static string YamlStr(string s) => (s ?? "").Replace("\"", "'").Replace("\n", " ");
    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}
