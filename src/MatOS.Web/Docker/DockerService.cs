using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace MatOS.Web.Docker;

/// <summary>
/// Thin wrapper over the local Docker Engine (via the mounted socket) using Docker.DotNet.
/// matOS is a control panel for Docker, so this owns listing/inspecting containers plus the
/// basic lifecycle actions and the log/stats streams. Failures degrade gracefully (empty
/// inventory + <see cref="LastError"/>) so the desktop still renders when Docker is unreachable.
/// </summary>
public partial class DockerService
{
    private readonly string _endpoint;
    private readonly ILogger<DockerService> _log;

    /// <summary>Host path where Docker stores named-volume data (bind-mounted into matOS
    /// so the File Explorer / volume sizing can read it). One dir per volume, data under _data.</summary>
    public string VolumesPath { get; }

    public DockerService(IConfiguration config, ILogger<DockerService> log)
    {
        _endpoint = config["MatOS:Docker:Endpoint"]
                    ?? Environment.GetEnvironmentVariable("MATOS_DOCKER_ENDPOINT")
                    ?? "unix:///var/run/docker.sock";
        VolumesPath = config["MatOS:Docker:VolumesPath"]
                    ?? Environment.GetEnvironmentVariable("MATOS_DOCKER_VOLUMES_PATH")
                    ?? "/var/lib/docker/volumes";
        _log = log;
    }

    public string? LastError { get; private set; }

    private DockerClient CreateClient() =>
        new DockerClientConfiguration(new Uri(_endpoint)).CreateClient();

    /// <summary>Exposed for other services (UpdateService) that need low-level Docker access.</summary>
    public DockerClient CreateRawClient() => CreateClient();

    private string? _selfStack;
    private bool _selfResolved;
    /// <summary>The Compose project (stack name) that matOS itself runs in — i.e. the bundled
    /// matOS + Caddy + Matcad stack — resolved by inspecting matOS's own container. Null when matOS
    /// isn't part of a Compose stack (e.g. dev). The desktop hides this stack (it's infrastructure).</summary>
    public async Task<string?> GetSelfStackAsync(CancellationToken ct = default)
    {
        if (_selfResolved) return _selfStack;
        try
        {
            var host = System.Net.Dns.GetHostName(); // in Docker this is the container's short id by default
            using var client = CreateClient();
            var list = await client.Containers.ListContainersAsync(new global::Docker.DotNet.Models.ContainersListParameters { All = true }, ct);
            var me = list.FirstOrDefault(c => (c.ID ?? "").StartsWith(host, StringComparison.OrdinalIgnoreCase))
                  ?? list.FirstOrDefault(c => c.Names != null && c.Names.Any(n => n.TrimStart('/').Equals("matos", StringComparison.OrdinalIgnoreCase)));
            if (me?.Labels != null && me.Labels.TryGetValue("com.docker.compose.project", out var proj) && !string.IsNullOrWhiteSpace(proj))
                _selfStack = proj;
        }
        catch { _selfStack = null; }
        _selfResolved = true;
        return _selfStack;
    }

    /// <summary>Starts an on-demand stack and waits until its UI port accepts a connection (so the
    /// window can load the app instead of a connection error). Returns true when started; readiness
    /// polling is best-effort with a timeout. matOS shares the app network, so it reaches the UI
    /// container by name:internal-port.</summary>
    public async Task<bool> WakeStackAsync(string name, CancellationToken ct = default)
    {
        var s0 = await GetStackAsync(name, ct);
        if (s0 == null) return false;
        await StackActionAsync(name, "start", ct);

        // Re-fetch so the now-running UI container reports its published host port, then poll it via
        // host.docker.internal (reachable for both image and compose apps).
        var s = await GetStackAsync(name, ct) ?? s0;
        var ui = s.Containers.FirstOrDefault(c => c.Labels.ContainsKey(MatcadLabels.Port));
        if (ui == null || !int.TryParse(ui.Labels.GetValueOrDefault(MatcadLabels.Port), out var internalPort))
            return true;
        var pub = ui.Ports.FirstOrDefault(p => p.PublicPort is > 0 && p.PrivatePort == internalPort)?.PublicPort
               ?? ui.Ports.FirstOrDefault(p => p.PublicPort is > 0)?.PublicPort;
        if (pub is not > 0) return true;

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync("host.docker.internal", pub.Value);
                if (await Task.WhenAny(connect, Task.Delay(1200, ct)) == connect && tcp.Connected) return true;
            }
            catch { /* not up yet */ }
            try { await Task.Delay(500, ct); } catch { break; }
        }
        return true;
    }

    private static readonly HttpClient _frameHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    /// <summary>Checks whether a stack's UI can be embedded in a matOS window by reading the app's
    /// framing headers (X-Frame-Options / CSP frame-ancestors) directly from the container. Returns
    /// (embeddable, reason). matOS shares the app network, so it reaches the container by name.</summary>
    public async Task<(bool Embeddable, string? Reason)> CheckFramingAsync(string name, CancellationToken ct = default)
    {
        var s = await GetStackAsync(name, ct);
        if (s == null) return (true, null);
        var ui = s.Containers.FirstOrDefault(c => c.Labels.ContainsKey(MatcadLabels.Port));
        if (ui == null || !int.TryParse(ui.Labels.GetValueOrDefault(MatcadLabels.Port), out var internalPort))
            return (true, null);
        // Reach the app via its published host port through host.docker.internal — works for both
        // image apps (on the matOS network) and compose apps (on their own project network).
        var pub = ui.Ports.FirstOrDefault(p => p.PublicPort is > 0 && p.PrivatePort == internalPort)?.PublicPort
               ?? ui.Ports.FirstOrDefault(p => p.PublicPort is > 0)?.PublicPort;
        if (pub is not > 0) return (true, null);
        try
        {
            using var resp = await _frameHttp.GetAsync($"http://host.docker.internal:{pub}/", HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.Headers.TryGetValues("X-Frame-Options", out var xfo))
            {
                var v = string.Join(",", xfo).ToLowerInvariant();
                if (v.Contains("deny")) return (false, "X-Frame-Options: DENY");
                if (v.Contains("sameorigin")) return (false, "X-Frame-Options: SAMEORIGIN");
            }
            IEnumerable<string>? csp = null;
            if (resp.Headers.TryGetValues("Content-Security-Policy", out var c1)) csp = c1;
            else if (resp.Content.Headers.TryGetValues("Content-Security-Policy", out var c2)) csp = c2;
            if (csp != null)
            {
                var v = string.Join(";", csp).ToLowerInvariant();
                var i = v.IndexOf("frame-ancestors", StringComparison.Ordinal);
                if (i >= 0 && v[i..].Contains("'none'")) return (false, "CSP frame-ancestors 'none'");
            }
            return (true, null);
        }
        catch { return (true, null); } // couldn't check — let the window try to load it
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            await client.System.PingAsync(ct);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // --- Inventory ----------------------------------------------------------

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all = true, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            var list = await client.Containers.ListContainersAsync(new ContainersListParameters { All = all }, ct);
            LastError = null;
            return list.Select(Map).OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning(ex, "Docker list failed ({Endpoint})", _endpoint);
            return Array.Empty<ContainerInfo>();
        }
    }

    public async Task<ContainerInfo?> GetContainerAsync(string id, CancellationToken ct = default)
    {
        var all = await ListContainersAsync(true, ct);
        return all.FirstOrDefault(c => c.Id == id || c.ShortId == id || c.Name == id);
    }

    private static ContainerInfo Map(ContainerListResponse c)
    {
        var labels = c.Labels != null
            ? new Dictionary<string, string>(c.Labels)
            : new Dictionary<string, string>();
        var name = (c.Names?.FirstOrDefault() ?? "").TrimStart('/');
        var ports = (c.Ports ?? new List<Port>())
            .Select(p => new PortMapping(p.Type ?? "tcp", (int)p.PrivatePort,
                p.PublicPort > 0 ? (int)p.PublicPort : null, string.IsNullOrEmpty(p.IP) ? null : p.IP))
            .ToList();

        labels.TryGetValue(MatcadLabels.Host, out var webHost);
        var managed = labels.TryGetValue(MatosLabels.Managed, out var m) && m == "true";

        return new ContainerInfo(
            Id: c.ID,
            ShortId: (c.ID ?? "").Length >= 12 ? c.ID![..12] : c.ID ?? "",
            Name: name,
            Image: c.Image ?? "",
            State: c.State ?? "",
            Status: c.Status ?? "",
            CreatedUtc: c.Created,
            Ports: ports,
            Labels: labels,
            WebHost: string.IsNullOrWhiteSpace(webHost) ? null : webHost,
            MatosManaged: managed,
            Networks: c.NetworkSettings?.Networks?.Keys.ToList() ?? new List<string>());
    }

    // --- Lifecycle ----------------------------------------------------------

    public async Task StartAsync(string id, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);
    }

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
    }

    public async Task RestartAsync(string id, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.Containers.RestartContainerAsync(id, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, ct);
    }

    // --- Logs ---------------------------------------------------------------

    public async Task<string> GetLogsAsync(string id, int tail = 200, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var tty = await IsTtyAsync(client, id, ct);
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true, ShowStderr = true, Follow = false,
            Tail = tail.ToString(), Timestamps = false
        };
        using var stream = await client.Containers.GetContainerLogsAsync(id, tty, parameters, ct);
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
        }
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> FollowLogsAsync(
        string id, int tail = 200, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var client = CreateClient();
        var tty = await IsTtyAsync(client, id, ct);
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true, ShowStderr = true, Follow = true,
            Tail = tail.ToString(), Timestamps = false
        };
        using var stream = await client.Containers.GetContainerLogsAsync(id, tty, parameters, ct);
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            MultiplexedStream.ReadResult read;
            try { read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct); }
            catch (OperationCanceledException) { break; }
            if (read.EOF) break;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            int nl;
            while ((nl = IndexOfNewline(sb)) >= 0)
            {
                var line = sb.ToString(0, nl).TrimEnd('\r');
                sb.Remove(0, nl + 1);
                yield return line;
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    private static async Task<bool> IsTtyAsync(DockerClient client, string id, CancellationToken ct)
    {
        try { var i = await client.Containers.InspectContainerAsync(id, ct); return i.Config?.Tty ?? false; }
        catch { return false; }
    }

    // --- Stats --------------------------------------------------------------

    public async IAsyncEnumerable<ContainerStatSample> FollowStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var client = CreateClient();
        var channel = Channel.CreateBounded<ContainerStatSample>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        var progress = new Progress<ContainerStatsResponse>(r =>
        {
            var sample = MapStats(r);
            if (sample != null) channel.Writer.TryWrite(sample);
        });

        _ = client.Containers
            .GetContainerStatsAsync(id, new ContainerStatsParameters { Stream = true }, progress, ct)
            .ContinueWith(t => channel.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        await foreach (var s in channel.Reader.ReadAllAsync(ct))
            yield return s;
    }

    private static ContainerStatSample? MapStats(ContainerStatsResponse r)
    {
        if (r?.CPUStats?.CPUUsage == null || r.PreCPUStats?.CPUUsage == null) return null;

        double cpuDelta = (double)r.CPUStats.CPUUsage.TotalUsage - r.PreCPUStats.CPUUsage.TotalUsage;
        double systemDelta = (double)r.CPUStats.SystemUsage - r.PreCPUStats.SystemUsage;
        double cpus = r.CPUStats.OnlineCPUs > 0
            ? r.CPUStats.OnlineCPUs
            : (r.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
        double cpuPercent = 0;
        if (systemDelta > 0 && cpuDelta > 0)
            cpuPercent = Math.Round(cpuDelta / systemDelta * cpus * 100.0, 1);

        long mem = (long)(r.MemoryStats?.Usage ?? 0);
        if (r.MemoryStats?.Stats != null && r.MemoryStats.Stats.TryGetValue("cache", out var cache))
            mem -= (long)cache;
        long limit = (long)(r.MemoryStats?.Limit ?? 0);

        return new ContainerStatSample(cpuPercent, Math.Max(mem, 0), limit);
    }
}
