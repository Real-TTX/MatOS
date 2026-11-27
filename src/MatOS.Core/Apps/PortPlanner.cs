using System.Text.RegularExpressions;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Apps;

/// <summary>A host port that an install would publish but that another container already holds.</summary>
public sealed record PortConflict(int Port, string Protocol, string Container);

/// <summary>Chooses the host port for an app UI and finds clashes of the ports an app publishes.</summary>
public sealed partial class PortPlanner
{
    private readonly MatOSDbContext _db;
    private readonly DockerService _docker;
    private readonly MatOSOptions _options;
    private readonly MatDockClient _matDock;

    public PortPlanner(MatOSDbContext db, DockerService docker, IOptions<MatOSOptions> options, MatDockClient matDock)
    {
        _db = db;
        _docker = docker;
        _options = options.Value;
        _matDock = matDock;
    }

    /// <summary>
    /// The host ports published now where an app on <paramref name="node"/> would publish its own: this machine (its proxy may serve a port route with the same
    /// number), and the node itself as MatDock lists its containers (docs/ARCHITECTURE.md "Nodes").
    /// </summary>
    private async Task<List<PublishedPort>> PublishedAsync(long? node, CancellationToken ct)
    {
        var ports = await _docker.ListPublishedPortsAsync(ct);
        if (node is not null)
        {
            foreach (var container in (await _matDock.ListContainersAsync(ct, null, node) ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var name = container["name"]?.GetValue<string>() ?? string.Empty;
                ports.AddRange(DockerService.ParsePorts(name, container["ports"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null));
            }
        }

        return ports;
    }

    /// <summary>
    /// The preferred port of the catalog entry when it is free, otherwise the first free port of the pool.
    /// A port counts as taken when another installed app was given it or a running container publishes it.
    /// </summary>
    public async Task<int?> PlanHostPortAsync(CatalogApp app, int selfPort, CancellationToken ct = default, long? node = null)
    {
        if (app.Ui is null)
        {
            return null;
        }

        var taken = new HashSet<int>(await _db.InstalledApps.Where(a => a.HostPort != null).Select(a => a.HostPort!.Value).ToListAsync(ct))
        {
            selfPort,
        };
        foreach (var published in await PublishedAsync(node, ct))
        {
            taken.Add(published.HostPort);
        }

        if (app.Ui.HostPort is { } preferred && !taken.Contains(preferred))
        {
            return preferred;
        }

        for (var port = _options.HostPortRangeStart; port <= _options.HostPortRangeEnd; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free host port is left in the configured range.");
    }

    /// <summary>
    /// The first host port from <paramref name="from"/> upward that no container publishes, no installed app was given and that is not one of
    /// <paramref name="alsoTaken"/>; null when none is free in the next 2000.
    /// </summary>
    public async Task<int?> FreePortFromAsync(int from, IReadOnlySet<int> alsoTaken, CancellationToken ct = default, long? node = null)
    {
        var taken = new HashSet<int>(await _db.InstalledApps.Where(a => a.HostPort != null).Select(a => a.HostPort!.Value).ToListAsync(ct));
        taken.UnionWith(alsoTaken);
        foreach (var published in await PublishedAsync(node, ct))
        {
            taken.Add(published.HostPort);
        }

        for (var port = Math.Max(from, 1024); port <= Math.Min(from + 2000, 65535); port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        return null;
    }

    /// <summary>
    /// The variable that names the host side of a published port (<c>"${SUBMISSION_PORT:-2587}:2587"</c> gives <c>SUBMISSION_PORT</c>), or null when the
    /// host side is a number (<c>"80:80"</c>): only a port a variable names can be moved.
    /// </summary>
    public static string? HostPortVariable(string raw)
    {
        var slash = raw.LastIndexOf('/');
        var match = HostVariableRegex().Match(slash >= 0 ? raw[..slash] : raw);
        return match.Success ? match.Groups["key"].Value : null;
    }

    [GeneratedRegex(@"^(?:[^:\$]+:)?\$\{(?<key>[A-Za-z_][A-Za-z0-9_]*)(?::?-[^}]*)?\}:[^:]+$")]
    private static partial Regex HostVariableRegex();

    /// <summary>The container of another project that publishes this host port now, or null when the port is free (or this project's own).</summary>
    public async Task<string?> HolderOfAsync(int hostPort, string ownProject, CancellationToken ct = default, long? node = null)
        => (await PublishedAsync(node, ct))
            .FirstOrDefault(p => p.HostPort == hostPort && !p.Container.StartsWith(ownProject, StringComparison.Ordinal))?.Container;

    /// <summary>The host ports the compose file publishes by itself (with the variables applied) that a running container already uses.</summary>
    public async Task<List<PortConflict>> FindConflictsAsync(CatalogApp app, IReadOnlyDictionary<string, string> variables, string ownProject, CancellationToken ct = default, long? node = null)
    {
        var conflicts = new List<PortConflict>();
        var published = node is null ? await _docker.ListPublishedPortsAsync(ct) : await NodeOnlyAsync(node.Value, ct);
        foreach (var fixedPort in app.FixedPorts)
        {
            var resolved = CatalogParser.ParsePort(EnvSubstitution.Apply(fixedPort.Raw, variables));
            if (resolved is null)
            {
                continue;
            }

            var holder = published.FirstOrDefault(p =>
                p.HostPort == resolved.HostPort &&
                p.Protocol.Equals(resolved.Protocol, StringComparison.OrdinalIgnoreCase) &&
                !p.Container.StartsWith(ownProject, StringComparison.Ordinal));
            if (holder is not null)
            {
                conflicts.Add(new PortConflict(resolved.HostPort, resolved.Protocol, holder.Container));
            }
        }

        // 443 over TCP and UDP is one problem for the reader: say it once per port and container.
        return conflicts.DistinctBy(c => (c.Port, c.Container)).ToList();
    }

    /// <summary>The host ports published on one other node (a port an app publishes itself clashes there only).</summary>
    private async Task<List<PublishedPort>> NodeOnlyAsync(long node, CancellationToken ct)
        => (await PublishedAsync(node, ct)).Except(await _docker.ListPublishedPortsAsync(ct)).ToList();
}
