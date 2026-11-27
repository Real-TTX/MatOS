using System.Text.RegularExpressions;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Docker;

/// <summary>
/// Typed Docker operations (containers, networks, volumes, ports) on top of <see cref="DockerCli"/>.
/// MatOS finds "its" containers by the labels it sets (<c>matos.managed</c>, <c>matos.instance</c>)
/// and by the compose project label, so Docker itself stays the source of truth for what runs. The containers of an install of
/// Mattiverse carry <c>mattiverse.*</c> until they are deployed again: those count as well (<see cref="AllLabels"/>).
/// </summary>
public sealed partial class DockerService
{
    public const string ManagedLabel = "matos.managed";
    public const string InstanceLabel = "matos.instance";
    public const string ServiceLabel = "matos.service";
    public const string AppLabel = "matos.app";
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>The labels MatOS sets on the containers it creates.</summary>
    public static readonly ContainerLabels Labels = new(ManagedLabel, InstanceLabel, ServiceLabel, AppLabel);

    /// <summary>The labels Mattiverse set (the name before 2026-11).</summary>
    public static readonly ContainerLabels LegacyLabels = new(
        Mattiverse.LabelPrefix + "managed", Mattiverse.LabelPrefix + "instance", Mattiverse.LabelPrefix + "service", Mattiverse.LabelPrefix + "app");

    /// <summary>Every set of labels that marks a container of MatOS, the current one first.</summary>
    public static readonly IReadOnlyList<ContainerLabels> AllLabels = [Labels, LegacyLabels];

    private readonly DockerCli _cli;
    private readonly MatOSOptions _options;
    private readonly ILogger<DockerService> _logger;
    private string? _selfName;
    private bool _selfResolved;

    public DockerService(DockerCli cli, IOptions<MatOSOptions> options, ILogger<DockerService> logger)
    {
        _cli = cli;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------ engine

    public async Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _cli.RunAsync(
                ["info", "--format", "{{.ServerVersion}}|{{.OperatingSystem}}|{{.NCPU}}|{{.MemTotal}}|{{.ContainersRunning}}|{{.Containers}}|{{.Images}}"],
                ct: ct);
            if (!result.Success)
            {
                return Unavailable(result.Message);
            }

            var parts = result.StdOut.Trim().Split('|');
            if (parts.Length < 7)
            {
                return Unavailable("Unexpected output of docker info.");
            }

            return new DockerEngineInfo(
                true,
                null,
                parts[0],
                parts[1],
                ToInt(parts[2]),
                ToLong(parts[3]),
                ToInt(parts[4]),
                ToInt(parts[5]),
                ToInt(parts[6]));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Unavailable("The docker CLI is not available: " + ex.Message);
        }
    }

    private static DockerEngineInfo Unavailable(string error)
        => new(false, error, string.Empty, string.Empty, 0, 0, 0, 0, 0);

    // ------------------------------------------------------------------ network

    /// <summary>Creates the shared network if needed and connects MatOS itself to it, so it can reach the apps by container name.</summary>
    public async Task EnsureNetworkAsync(CancellationToken ct = default)
    {
        var name = _options.NetworkName;
        var inspect = await _cli.RunAsync(["network", "inspect", name, "--format", "{{.Id}}"], ct: ct);
        if (!inspect.Success)
        {
            var arguments = new List<string> { "network", "create", "--label", $"{ManagedLabel}=true" };
            if (!string.IsNullOrWhiteSpace(_options.NetworkSubnet))
            {
                arguments.AddRange(["--subnet", _options.NetworkSubnet.Trim()]);
            }

            arguments.Add(name);
            var create = await _cli.RunAsync(arguments, ct: ct);
            if (!create.Success && !create.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                var hint = create.Message.Contains("pool", StringComparison.OrdinalIgnoreCase)
                    ? " Docker has run out of network addresses: set MatOS__NetworkSubnet (like 10.253.200.0/24) or free unused networks with 'docker network prune'."
                    : string.Empty;
                throw new InvalidOperationException($"Could not create the Docker network '{name}': {create.Message}{hint}");
            }
        }

        var self = await GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return;
        }

        var connect = await _cli.RunAsync(ConnectArguments(name, self), ct: ct);
        if (!connect.Success && !connect.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Could not connect MatOS to the network '{Network}': {Message}", name, connect.Message);
        }
    }

    /// <summary>
    /// The arguments that attach MatOS (<paramref name="self"/>) to the shared network. On the network of an install of Mattiverse it also answers to the name
    /// its container had then (<c>matos</c> was <c>mattiverse</c>, <c>matos-dev</c> was <c>mattiverse-dev</c>): what the engines keep of it (Matcad's route to
    /// <c>http://mattiverse:8030</c>, the back-channel address in MatIdentity) keeps working until the engines learn the new name (<see cref="Configuration.RenameFollowUp"/>).
    /// </summary>
    public static List<string> ConnectArguments(string network, string self)
    {
        const string ContainerName = "matos";
        var arguments = new List<string> { "network", "connect" };
        if (string.Equals(network, Mattiverse.Name, StringComparison.Ordinal) && self.StartsWith(ContainerName, StringComparison.Ordinal))
        {
            arguments.AddRange(["--alias", Mattiverse.Name + self[ContainerName.Length..]]);
        }

        arguments.AddRange([network, self]);
        return arguments;
    }

    /// <summary>The name of the container MatOS runs in, or <c>null</c> when it does not run in Docker (local development).</summary>
    public async Task<string?> GetSelfContainerNameAsync(CancellationToken ct = default)
    {
        if (_selfResolved)
        {
            return _selfName;
        }

        _selfResolved = true;
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname) || !File.Exists("/.dockerenv"))
        {
            return null;
        }

        var inspect = await _cli.RunAsync(["inspect", "--format", "{{.Name}}", hostname], ct: ct);
        if (inspect.Success)
        {
            _selfName = inspect.StdOut.Trim().TrimStart('/');
        }

        return _selfName;
    }

    /// <summary>The port of the host that MatOS's own <paramref name="containerPort"/> is published on, or <c>null</c> outside Docker or when it is not published.</summary>
    public async Task<int?> GetSelfHostPortAsync(int containerPort, CancellationToken ct = default)
    {
        var self = await GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return null;
        }

        var inspect = await _cli.RunAsync(["inspect", "--format", "{{json .NetworkSettings.Ports}}", self], ct: ct);
        return inspect.Success ? ParseHostPort(inspect.StdOut, containerPort) : null;
    }

    /// <summary>The first host port of <c>&lt;containerPort&gt;/tcp</c> in the JSON of <c>.NetworkSettings.Ports</c> (<c>{"8030/tcp":[{"HostIp":"0.0.0.0","HostPort":"8030"}]}</c>), or <c>null</c>.</summary>
    public static int? ParseHostPort(string? json, int containerPort)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty($"{containerPort}/tcp", out var bindings)
                || bindings.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.ValueKind == System.Text.Json.JsonValueKind.Object
                    && binding.TryGetProperty("HostPort", out var hostPort)
                    && int.TryParse(hostPort.GetString(), out var port) && port > 0)
                {
                    return port;
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ containers

    /// <summary>
    /// All containers that carry the MatOS label (or the one of Mattiverse), optionally only those of one instance. <c>docker ps</c> cannot OR two
    /// filters, so each set of labels is asked on its own and the answers are merged.
    /// </summary>
    public async Task<List<ContainerInfo>> ListContainersAsync(string? instance = null, CancellationToken ct = default)
    {
        var outputs = new List<string>();
        foreach (var labels in AllLabels)
        {
            var result = await _cli.RunAsync(ListArguments(labels, instance), ct: ct);
            if (!result.Success)
            {
                _logger.LogWarning("docker ps failed: {Message}", result.Message);
                return [];
            }

            outputs.Add(result.StdOut);
        }

        // A second MatOS on the same Docker (a test instance with its own prefix) uses the same labels and slugs: only this one's projects count.
        return MergeContainers(outputs).Where(c => IsOwn(c, _options.ProjectPrefix)).ToList();
    }

    /// <summary>The arguments of <c>docker ps</c> for the containers marked with <paramref name="labels"/> (of one instance), read with the same labels.</summary>
    public static List<string> ListArguments(ContainerLabels labels, string? instance)
    {
        var args = new List<string> { "ps", "-a", "--no-trunc", "--filter", $"label={labels.Managed}=true" };
        if (instance is not null)
        {
            args.AddRange(["--filter", $"label={labels.Instance}={instance}"]);
        }

        args.AddRange(["--format", $"{{{{.ID}}}}\t{{{{.Names}}}}\t{{{{.Image}}}}\t{{{{.State}}}}\t{{{{.Status}}}}\t{{{{.Label \"{labels.Instance}\"}}}}\t{{{{.Label \"{labels.Service}\"}}}}\t{{{{.Label \"{ComposeProjectLabel}\"}}}}\t{{{{.Ports}}}}"]);
        return args;
    }

    /// <summary>The containers of several answers of <c>docker ps</c>, each once (a container with both sets of labels is in two answers).</summary>
    public static List<ContainerInfo> MergeContainers(IEnumerable<string> outputs)
        => outputs.SelectMany(ParseContainers).DistinctBy(c => c.Id).ToList();

    /// <summary>A container of this MatOS: its compose project starts with the prefix (one without a project is kept, as before).</summary>
    public static bool IsOwn(ContainerInfo container, string prefix)
        => string.IsNullOrEmpty(container.Project) || container.Project.StartsWith(prefix + "-", StringComparison.Ordinal);

    /// <summary>
    /// Reads the tab separated output of <c>docker ps</c>. The last field (the ports) is empty for a stopped container, so a
    /// line must not be trimmed: that would drop the trailing tab and with it the whole container.
    /// </summary>
    public static List<ContainerInfo> ParseContainers(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(p => p.Length >= 8)
            .Select(p => p.Length >= 9
                ? new ContainerInfo(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[8], p[7])
                : new ContainerInfo(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]))
            .ToList();

    /// <summary>Summarizes the containers of one app as a single state.</summary>
    public static RuntimeInfo Summarize(IReadOnlyCollection<ContainerInfo> containers)
    {
        if (containers.Count == 0)
        {
            return new RuntimeInfo(RuntimeState.Missing, 0, 0, "Not created");
        }

        var running = containers.Count(c => c.IsRunning);
        var broken = containers.Any(c =>
            c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase) ||
            c.State.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
            c.Status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) ||
            (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && !c.Status.Contains("(0)", StringComparison.Ordinal)));

        if (broken)
        {
            return new RuntimeInfo(RuntimeState.Unhealthy, running, containers.Count, "Needs attention");
        }

        if (running == containers.Count)
        {
            return new RuntimeInfo(RuntimeState.Running, running, containers.Count, "Running");
        }

        return running == 0
            ? new RuntimeInfo(RuntimeState.Stopped, 0, containers.Count, "Stopped")
            : new RuntimeInfo(RuntimeState.Partial, running, containers.Count, $"{running} of {containers.Count} running");
    }

    /// <summary>The last lines of a container's log.</summary>
    public async Task<string> GetLogsAsync(string container, int tail = 300, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["logs", "--tail", tail.ToString(), "--timestamps", container], ct: ct);

        // docker logs writes the container's stderr to its own stderr: both are log content.
        return result.Success ? (result.StdOut + result.StdErr).TrimEnd() : result.Message;
    }

    /// <summary>The ports that the containers of a project publish are read from the labelled containers; this lists every published host port of the engine.</summary>
    public async Task<List<PublishedPort>> ListPublishedPortsAsync(CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["ps", "--format", "{{.Names}}\t{{.Ports}}"], ct: ct);
        var ports = new List<PublishedPort>();
        if (!result.Success)
        {
            return ports;
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            foreach (Match match in PublishedPortRegex().Matches(parts[1]))
            {
                var first = int.Parse(match.Groups["from"].Value);
                var last = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value) : first;
                var protocol = match.Groups["proto"].Value;
                for (var port = first; port <= last && port - first < 1000; port++)
                {
                    ports.Add(new PublishedPort(port, protocol, parts[0]));
                }
            }
        }

        return ports;
    }

    // ------------------------------------------------------------------ volumes

    /// <summary>The named volumes that compose created for a project (without external volumes).</summary>
    public async Task<List<string>> ListProjectVolumesAsync(string project, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(
            ["volume", "ls", "--filter", $"label={ComposeProjectLabel}={project}", "--format", "{{.Name}}"],
            ct: ct);
        return result.Success
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    public async Task<bool> VolumeExistsAsync(string name, CancellationToken ct = default)
        => (await _cli.RunAsync(["volume", "inspect", name], ct: ct)).Success;

    /// <summary>Deletes a volume (no container may use it). False when Docker refused.</summary>
    public async Task<bool> RemoveVolumeAsync(string name, CancellationToken ct = default)
        => (await _cli.RunAsync(["volume", "rm", name], ct: ct)).Success;

    /// <summary>Creates a volume with the labels compose expects, so <c>docker compose up</c> adopts it without a warning.</summary>
    public async Task CreateProjectVolumeAsync(string project, string logicalName, CancellationToken ct = default)
    {
        var fullName = $"{project}_{logicalName}";
        var result = await _cli.RunAsync(
            ["volume", "create", "--label", $"{ComposeProjectLabel}={project}", "--label", $"com.docker.compose.volume={logicalName}", fullName],
            ct: ct);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not create the volume '{fullName}': {result.Message}");
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>The image id (sha256:...) of a locally available image reference, or <c>null</c>.</summary>
    public async Task<string?> GetImageIdAsync(string imageReference, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["image", "inspect", "--format", "{{.Id}}", imageReference], ct: ct);
        return result.Success ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// The image a container was created from as its compose file names it (<c>Config.Image</c>, the tag) and the id (sha256:...) of the image it runs,
    /// or <c>null</c>. <c>docker ps</c> is no help here: once the tag points to a newer image it shows the old id instead of the tag.
    /// </summary>
    public async Task<(string Reference, string ImageId)?> GetContainerImageAsync(string container, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["inspect", "--format", "{{.Config.Image}} {{.Image}}", container], ct: ct);
        var parts = result.Success ? result.StdOut.Trim().Split(' ', 2) : [];
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>Every repo digest (<c>repo@sha256:...</c>) of an image: one per repository it was pulled from; empty for an image built here.</summary>
    public async Task<List<string>> GetRepoDigestsAsync(string image, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["image", "inspect", "--format", "{{json .RepoDigests}}", image], ct: ct);
        return result.Success ? ParseRepoDigests(result.StdOut) : [];
    }

    /// <summary>Reads <c>{{json .RepoDigests}}</c> (<c>["ghcr.io/x/y@sha256:..."]</c>, or <c>null</c>/<c>[]</c>).</summary>
    public static List<string> ParseRepoDigests(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json.Trim()) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The repo digest (<c>repo@sha256:...</c>) of a container's image, if it came from a registry.</summary>
    public async Task<string?> GetContainerImageDigestAsync(string container, CancellationToken ct = default)
    {
        var result = await _cli.RunAsync(["inspect", "--format", "{{.Image}}", container], ct: ct);
        if (!result.Success)
        {
            return null;
        }

        var image = await _cli.RunAsync(["image", "inspect", "--format", "{{if .RepoDigests}}{{index .RepoDigests 0}}{{end}}", result.StdOut.Trim()], ct: ct);
        var digest = image.Success ? image.StdOut.Trim() : null;
        return string.IsNullOrEmpty(digest) ? null : digest;
    }

    // ------------------------------------------------------------------ helpers

    private static int ToInt(string value) => int.TryParse(value, out var n) ? n : 0;

    private static long ToLong(string value) => long.TryParse(value, out var n) ? n : 0;

    // 0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 0.0.0.0:21000-21049->21000-21049/tcp
    [GeneratedRegex(@":(?<from>\d+)(?:-(?<to>\d+))?->\d+(?:-\d+)?/(?<proto>tcp|udp)")]
    private static partial Regex PublishedPortRegex();
}
