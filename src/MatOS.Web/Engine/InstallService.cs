using System.Diagnostics;
using System.Text;
using Docker.DotNet.Models;
using MatOS.Web.Config;
using MatOS.Web.Docker;
using MatOS.Web.Services;

namespace MatOS.Web.Engine;

/// <summary>Port pool + per-app instance counter. Persisted as store.json.</summary>
public class InstallStore
{
    public int NextPort { get; set; } = 20000;
    public Dictionary<string, int> Counters { get; set; } = new();
    public List<string> Registered { get; set; } = new();   // handler/opener app ids registered for "Open with"
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

    /// <summary>The Docker network app containers join so the reverse proxy can reach them.
    /// Configurable in Settings; empty setting falls back to the built-in default.</summary>
    private string ProxyNetwork
    {
        get { var n = _config.Get<SystemConfig>("system").Network; return string.IsNullOrWhiteSpace(n) ? _network : n.Trim(); }
    }

    public Task<InstallResult> InstallAsync(string appId, IDictionary<string, string>? vars, CancellationToken ct = default)
    {
        var app = _store.Find(appId);
        if (app == null) return Task.FromResult(new InstallResult(false, null, 0, "Unknown app."));
        // Handler/opener apps don't run a persistent container — installing them registers the
        // app so it appears in the File Explorer's "Open with" for its file types.
        if (app.Handlers is { Length: > 0 }) return RegisterAsync(app, ct);
        return string.Equals(app.Kind, "compose", StringComparison.OrdinalIgnoreCase)
            ? InstallComposeAsync(app, vars ?? new Dictionary<string, string>(), ct)
            : InstallImageAsync(app, vars ?? new Dictionary<string, string>(), ct);
    }

    // ---- Handler/opener registration (so "Open with" lists the app) ----
    public IReadOnlyList<string> RegisteredApps()
    {
        lock (_gate) return _config.Get<InstallStore>("store").Registered.ToList();
    }

    public bool IsRegistered(string appId)
    {
        lock (_gate) return _config.Get<InstallStore>("store").Registered.Contains(appId);
    }

    private async Task<InstallResult> RegisterAsync(AppDef app, CancellationToken ct)
    {
        lock (_gate)
        {
            var s = _config.Get<InstallStore>("store");
            if (!s.Registered.Contains(app.Id)) s.Registered.Add(app.Id);
        }
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        // Pre-pull so the first "Open with" is fast; failure is non-fatal (pulled on first open).
        try { await _docker.PullImageBestEffortAsync(app.Image, ct); } catch (Exception ex) { _log.LogWarning(ex, "Register pre-pull of {App} failed", app.Id); }
        _log.LogInformation("Registered handler app {App}", app.Id);
        return new(true, app.Id, 0, null);
    }

    public async Task<bool> UnregisterAsync(string appId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate) { var s = _config.Get<InstallStore>("store"); removed = s.Registered.Remove(appId); }
        if (removed) await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        return removed;
    }

    // setup-wizard variables -> env (used or default); lets image apps use the wizard too
    private static Dictionary<string, string> BuildEnv(AppDef app, IDictionary<string, string> vars)
    {
        var env = new Dictionary<string, string>(app.Env);
        foreach (var v in app.Variables)
        {
            var val = vars.TryGetValue(v.Key, out var provided) && !string.IsNullOrEmpty(provided) ? provided : v.Default;
            if (!string.IsNullOrEmpty(val)) env[v.Key] = val;
        }
        return env;
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
    private async Task<InstallResult> InstallImageAsync(AppDef app, IDictionary<string, string> vars, CancellationToken ct)
    {
        var instance = NextInstance(app.Id);
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        var port = await NextFreePortAsync(ct);
        var name = $"matos_{app.Id}_{instance}";
        try
        {
            await _docker.EnsureNetworkAsync(ProxyNetwork, ct);
            await _docker.PullImageBestEffortAsync(app.Image, ct);

            var mounts = new List<Mount>();
            for (int i = 0; i < app.Volumes.Length; i++)
                mounts.Add(new Mount { Type = "volume", Source = $"{name}_data{i}", Target = app.Volumes[i] });

            var p = new CreateContainerParameters
            {
                Image = app.Image, Name = name,
                Env = BuildEnv(app, vars).Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                Labels = ManagedLabels(app, instance, ui: true),
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{app.UiPort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>> { [$"{app.UiPort}/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } } },
                    Mounts = mounts,
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    NetworkMode = ProxyNetwork
                }
            };
            await _docker.CreateAndStartAsync(p, ct);
            return new(true, name, port, null);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Install (image) of {App} failed", app.Id); return new(false, name, port, ex.Message); }
    }

    // Installs are internal by default (matcad.enable=false); "Publish" turns on the
    // reverse-proxy route + hostname. matcad.port is kept for when it gets published.
    private Dictionary<string, string> ManagedLabels(AppDef app, int instance, bool ui) => new()
    {
        ["matos.managed"] = "true",
        ["matos.app"] = app.Id,
        ["matos.instance"] = instance.ToString(),
        ["matos.title"] = app.Name,
        ["matcad.enable"] = "false",
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
                sb.AppendLine("      matcad.enable: \"false\"");
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

        try { await _docker.EnsureNetworkAsync(ProxyNetwork, ct); } catch { }

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

    // ---- Publish / unpublish (external reverse-proxy route via Matcad) ----
    public async Task<InstallResult> PublishAsync(string id, bool enabled, string? hostname, CancellationToken ct = default)
    {
        try
        {
            var detail = await _docker.InspectDetailAsync(id, ct);
            if (detail == null) return new(false, null, 0, "Container not found.");
            var labels = detail.Info.Labels;
            var appId = labels.GetValueOrDefault(MatosLabels.App, "");
            var instance = labels.GetValueOrDefault(MatosLabels.Instance, "");
            var baseDomain = _config.Get<SystemConfig>("system").BaseDomain;
            var host = !string.IsNullOrWhiteSpace(hostname) ? hostname!.Trim()
                     : $"{Slug(appId)}{(string.IsNullOrEmpty(instance) ? "" : "-" + instance)}.{baseDomain}";

            if (!string.IsNullOrEmpty(detail.ComposeProject) && detail.ComposeProject.StartsWith("matos-", StringComparison.Ordinal))
                await PublishComposeAsync(appId, detail.ComposeProject!, enabled, host, ct);
            else
            {
                var newId = await _docker.RecreateWithLabelsAsync(id, new Dictionary<string, string?>
                {
                    ["matcad.enable"] = enabled ? "true" : "false",
                    ["matcad.host"] = enabled ? host : null
                }, ct);
                // Make sure the app shares the proxy network so Caddy/Matcad can reach it by name.
                if (enabled)
                    try { await _docker.EnsureNetworkAsync(ProxyNetwork, ct); await _docker.ConnectNetworkAsync(newId, ProxyNetwork, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "Attaching {Id} to proxy network {Net} failed", newId, ProxyNetwork); }
            }

            return new(true, enabled ? host : null, 0, null);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Publish of {Id} failed", id); return new(false, null, 0, ex.Message); }
    }

    private async Task PublishComposeAsync(string appId, string project, bool enabled, string host, CancellationToken ct)
    {
        var app = _store.Find(appId) ?? throw new InvalidOperationException("App definition not found.");
        var services = _store.ParseServices(app.Compose);
        if (services.Count == 0) throw new InvalidOperationException("Compose has no services.");
        var ui = !string.IsNullOrWhiteSpace(app.UiService) && services.Contains(app.UiService) ? app.UiService : services[0];

        var all = await _docker.ListContainersAsync(true, ct);
        var uiC = all.FirstOrDefault(c => c.Labels.GetValueOrDefault(ComposeLabels.Project, "") == project
                                       && c.Labels.GetValueOrDefault(ComposeLabels.Service, "") == ui);
        var instance = uiC?.Labels.GetValueOrDefault(MatosLabels.Instance, "1") ?? "1";
        int port = uiC?.Ports.FirstOrDefault(p => p.PublicPort is > 0)?.PublicPort ?? await NextFreePortAsync(ct);

        var dir = Path.Combine(Path.GetTempPath(), "matos", project);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "docker-compose.yml"), app.Compose, ct);

        var sb = new StringBuilder(); sb.AppendLine("services:");
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
                sb.AppendLine($"      matcad.enable: \"{(enabled ? "true" : "false")}\"");
                sb.AppendLine($"      matcad.port: \"{app.UiPort}\"");
                if (enabled) sb.AppendLine($"      matcad.host: \"{host}\"");
                sb.AppendLine("    ports:");
                sb.AppendLine($"      - \"{port}:{app.UiPort}\"");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "matos-override.yml"), sb.ToString(), ct);
        if (!File.Exists(Path.Combine(dir, ".env"))) await File.WriteAllTextAsync(Path.Combine(dir, ".env"), "", ct);
        await RunCompose(dir, new[] { "-p", project, "-f", "docker-compose.yml", "-f", "matos-override.yml", "up", "-d", "--remove-orphans" }, null, ct);
    }

    // ---- "Open with" (file handlers): launch an on-demand container bound to a file ----
    public record OpenResult(bool Ok, string? ContainerId, int HostPort, string? Error, string? Title);

    /// <summary>Launch an ephemeral container that opens <paramref name="relPath"/> (inside
    /// <paramref name="volume"/>) with the handler app <paramref name="appId"/>. The file's volume is
    /// mounted at the handler's MountPath and the app is pointed at the file (env or command arg).</summary>
    public async Task<OpenResult> OpenWithAsync(string appId, string volume, string relPath, CancellationToken ct = default)
    {
        var app = _store.Find(appId);
        if (app == null) return new(false, null, 0, "Unknown app.", null);
        if (app.Handlers == null || app.Handlers.Length == 0) return new(false, null, 0, $"{app.Name} has no file handlers.", null);
        if (string.IsNullOrWhiteSpace(app.Image)) return new(false, null, 0, "Only single-image apps can open files.", null);

        var ext = NormExt(Path.GetExtension(relPath));
        var handler = app.Handlers.FirstOrDefault(h => h.Extensions.Any(e => NormExt(e) == ext));
        if (handler == null) return new(false, null, 0, $"{app.Name} does not handle {ext} files.", null);

        var relPosix = relPath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(relPosix);
        var mount = handler.MountPath.TrimEnd('/'); if (mount.Length == 0) mount = "/data";

        var instance = NextInstance($"open-{app.Id}");
        await _config.SaveAsync("store", _config.Get<InstallStore>("store"));
        var port = await NextFreePortAsync(ct);
        var name = $"matos-open-{Slug(app.Id)}-{instance}";
        var title = $"{app.Name} — {fileName}";
        try
        {
            await _docker.EnsureNetworkAsync(ProxyNetwork, ct);
            await _docker.PullImageBestEffortAsync(app.Image, ct);

            var env = new Dictionary<string, string>(app.Env);
            if (string.Equals(handler.Mechanism, "env", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(handler.EnvKey))
                env[handler.EnvKey] = relPosix;

            IList<string>? cmd = null;
            if (string.Equals(handler.Mechanism, "arg", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(handler.ArgTemplate))
                cmd = TokenizeArgs(handler.ArgTemplate.Replace("{file}", $"{mount}/{relPosix}").Replace("{port}", app.UiPort.ToString()));

            var p = new CreateContainerParameters
            {
                Image = app.Image, Name = name, Cmd = cmd,
                Env = env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                Labels = new Dictionary<string, string>
                {
                    ["matos.managed"] = "true",
                    ["matos.app"] = app.Id,
                    ["matos.instance"] = instance.ToString(),
                    ["matos.title"] = title,
                    ["matos.ephemeral"] = "true",
                    ["matos.openVolume"] = volume,
                    ["matos.openFile"] = relPosix,
                    ["matcad.enable"] = "false",
                    ["matcad.port"] = app.UiPort.ToString(),
                },
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{app.UiPort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>> { [$"{app.UiPort}/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } } },
                    Mounts = new List<Mount> { new() { Type = "volume", Source = volume, Target = mount, ReadOnly = handler.ReadOnly } },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
                    NetworkMode = ProxyNetwork
                }
            };
            var id = await _docker.CreateAndStartAsync(p, ct);
            // The container is "started" but the web server inside isn't listening yet — wait for it
            // so the window's iframe doesn't load into a connection-refused blank page.
            await WaitForReadyAsync(name, app.UiPort, TimeSpan.FromSeconds(15), ct);
            _log.LogInformation("Opened {File} in {App} as ephemeral {Name}", relPosix, app.Id, name);
            return new(true, id, port, null, title);
        }
        catch (Exception ex) { _log.LogWarning(ex, "OpenWith {App} failed", app.Id); return new(false, null, port, ex.Message, null); }
    }

    /// <summary>Remove an ephemeral "open with" container (only ones we marked ephemeral).</summary>
    public async Task<bool> CloseEphemeralAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var d = await _docker.InspectDetailAsync(id, ct);
            if (d == null || d.Info.Labels.GetValueOrDefault("matos.ephemeral") != "true") return false;
            await _docker.RemoveContainerAsync(id, force: true, ct);
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "CloseEphemeral {Id} failed", id); return false; }
    }

    /// <summary>Poll a just-started container's web port (by container name on the shared network)
    /// until it accepts connections, so we only hand the browser a URL that will actually load.</summary>
    private async Task WaitForReadyAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(1000);
                await tcp.ConnectAsync(host, port, cts.Token);
                if (tcp.Connected) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(250, ct);
        }
        _log.LogWarning("Ephemeral {Host}:{Port} not ready within {S}s", host, port, timeout.TotalSeconds);
    }

    // ---- helpers ----
    private static string NormExt(string e)
    {
        e = (e ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) return e;
        return e[0] == '.' ? e : "." + e;
    }

    private static List<string> TokenizeArgs(string s)
    {
        var list = new List<string>(); var sb = new StringBuilder(); char quote = '\0';
        foreach (var ch in s)
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; else sb.Append(ch); }
            else if (ch is '"' or '\'') quote = ch;
            else if (char.IsWhiteSpace(ch)) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

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
