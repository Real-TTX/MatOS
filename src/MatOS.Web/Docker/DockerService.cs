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
            MatosManaged: managed);
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
