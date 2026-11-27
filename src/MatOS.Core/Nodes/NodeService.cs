using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Nodes;

/// <summary>How busy a node was at MatDock's last reading (percent).</summary>
public sealed record NodeLoad(double CpuPercent, double MemoryPercent, double DiskPercent);

/// <summary>
/// A Docker host MatOS can run apps on: one of MatDock's environments (docs/ARCHITECTURE.md "Nodes"). <see cref="IsLocal"/> is this machine;
/// <see cref="Id"/> is MatDock's id of the environment, and an app on this machine stores <c>null</c> instead of it.
/// </summary>
public sealed record NodeInfo(
    long Id,
    string Name,
    bool IsLocal,
    string? Host,
    int? Port,
    string? User,
    string Status,
    bool Enabled,
    string? DockerVersion,
    string? ManagedBy,
    string? PublicKey,
    string? Message,
    NodeLoad? Load,
    int? ContainersRunning)
{
    /// <summary>What the pages call it: <c>This machine</c> for the local environment, else its name.</summary>
    public string DisplayName => IsLocal ? NodeService.ThisMachine : Name;

    public bool IsOnline => Status == "online" && Enabled;

    /// <summary>Added by MatOS (or Mattiverse): only such a node may be removed from MatOS.</summary>
    public bool IsOwn => Owner.IsOurs(ManagedBy);

    /// <summary>What an app stores for this node: null on this machine.</summary>
    public long? AppNodeId => IsLocal ? null : Id;

    /// <summary>The plain state: Online, Offline, Not checked yet, Off.</summary>
    public string StatusText => !Enabled ? "Switched off" : Status switch
    {
        "online" => "Online",
        "offline" => "Offline",
        "error" => "Error",
        _ => "Not checked yet",
    };
}

/// <summary>What adding a node or a new key answered: the public key to put on the host, and whether MatDock reached it already.</summary>
public sealed record NodeWrite(long Id, string Name, string? PublicKey, bool Connected, string? Message);

/// <summary>
/// The nodes: MatDock's environments, read and written through MatDock (MatOS keeps no list of its own). Read for a few seconds from a cache, so the pages
/// and the runtime reader ask MatDock once; a write forgets the cache.
/// </summary>
public sealed partial class NodeService
{
    /// <summary>The name of the local environment on the pages.</summary>
    public const string ThisMachine = "This machine";

    private const string CacheKey = "nodes:list";
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(15);

    private readonly MatDockClient _matDock;
    private readonly MatOSDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NodeService> _logger;

    public NodeService(MatDockClient matDock, MatOSDbContext db, IMemoryCache cache, ILogger<NodeService> logger)
    {
        _matDock = matDock;
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Every node MatDock knows, this machine first; empty when MatDock is not installed or does not answer (then everything runs on this machine).
    /// </summary>
    public async Task<IReadOnlyList<NodeInfo>> ListAsync(CancellationToken ct = default, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(CacheKey, out IReadOnlyList<NodeInfo>? known) && known is not null)
        {
            return known;
        }

        IReadOnlyList<NodeInfo> nodes = [];
        if (await _matDock.IsInstalledAsync(ct))
        {
            try
            {
                nodes = FromEnvironments(await _matDock.ListEnvironmentsAsync(ct));
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "MatDock did not list its environments.");
            }
        }

        _cache.Set(CacheKey, nodes, nodes.Count > 0 ? CacheTime : TimeSpan.FromSeconds(5));
        return nodes;
    }

    /// <summary>The nodes from MatDock's <c>GET /api/v1/environments</c>: the local one first, then the others by name.</summary>
    public static IReadOnlyList<NodeInfo> FromEnvironments(JsonArray? environments)
        => (environments ?? [])
            .OfType<JsonObject>()
            .Select(e =>
            {
                var reading = e["reading"] as JsonObject;
                return new NodeInfo(
                    (long)(Number(e, "id") ?? 0),
                    Text(e, "name") ?? "?",
                    Text(e, "type") == "local",
                    Text(e, "host"),
                    (int?)Number(e, "port"),
                    Text(e, "username"),
                    (Text(e, "status") ?? "unknown").ToLowerInvariant(),
                    e["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) ? on : true,
                    Text(e, "dockerVersion"),
                    Text(e, "managedBy"),
                    Text(e, "publicKey"),
                    Text(e, "message"),
                    reading is null ? null : new NodeLoad(Number(reading, "cpuPercent") ?? 0, Number(reading, "memoryPercent") ?? 0, Number(reading, "diskPercent") ?? 0),
                    reading is null ? null : (int?)Number(reading, "containersRunning"));
            })
            .Where(n => n.Id > 0)
            .OrderBy(n => n.IsLocal ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The nodes an app can be put on: this machine and every other node that is switched on (an offline one too: it may be back by the time).</summary>
    public async Task<IReadOnlyList<NodeInfo>> ChoicesAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).Where(n => n.Enabled && (n.IsLocal || n.Host is not null)).ToList();

    /// <summary>The node of an app (<c>null</c> = this machine), or null when MatDock does not know it (any more).</summary>
    public async Task<NodeInfo?> FindAsync(long? node, CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(n => node is null ? n.IsLocal : n.Id == node && !n.IsLocal);

    /// <summary>The name of an app's node for the pages: <c>This machine</c>, its name, or <c>Node &lt;id&gt;</c> while MatDock does not answer.</summary>
    public async Task<string> NameOfAsync(long? node, CancellationToken ct = default)
        => node is null ? ThisMachine : (await FindAsync(node, ct))?.Name ?? $"Node {node}";

    /// <summary>MatDock's id of this machine (the local environment), for a call that has to name it; null when MatDock does not answer.</summary>
    public async Task<long?> LocalIdAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(n => n.IsLocal)?.Id;

    /// <summary>
    /// Whether the pages may ask MatDock about a node now: this machine always, another node only while MatDock found it online, so a VM that is down
    /// never slows a page (MatDock would wait for SSH).
    /// </summary>
    public async Task<bool> IsReachableAsync(long? node, CancellationToken ct = default)
        => node is null || (await FindAsync(node, ct))?.IsOnline == true;

    /// <summary>The nodes (other than this machine) that have apps of MatOS: those the lists of containers and backups ask.</summary>
    public Task<List<long>> NodesWithAppsAsync(CancellationToken ct = default)
        => _db.InstalledApps.AsNoTracking().Where(a => a.NodeId != null).Select(a => a.NodeId!.Value).Distinct().ToListAsync(ct);

    // ------------------------------------------------------------------ writing (through MatDock)

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,39}$")]
    private static partial Regex NameRegex();

    /// <summary>The errors of the form to add a node (empty: it can be sent).</summary>
    public static List<string> Validate(string? name, string? host, int port, string? user)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || !NameRegex().IsMatch(name.Trim()))
        {
            errors.Add("The name has 1 to 40 letters, digits, spaces, dots, dashes or underscores and starts with a letter or digit.");
        }
        else if (string.Equals(name.Trim(), ThisMachine, StringComparison.OrdinalIgnoreCase) || string.Equals(name.Trim(), "local", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"\"{name.Trim()}\" is the name of this machine. Choose another one.");
        }

        if (string.IsNullOrWhiteSpace(host) || host.Trim().Length > 255 || host.Trim().Any(char.IsWhiteSpace) || host.Contains("://", StringComparison.Ordinal))
        {
            errors.Add("The address is the name or IP address of the VM, without spaces and without http://.");
        }

        if (port is < 1 or > 65535)
        {
            errors.Add("The SSH port is between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(user) || user.Trim().Length > 64 || user.Trim().Any(char.IsWhiteSpace))
        {
            errors.Add("The user is the name of the SSH user on the VM, without spaces.");
        }

        return errors;
    }

    /// <summary>
    /// Adds a node: MatDock makes a key pair and tries to connect (it cannot yet, before the key is on the VM). A name that MatDock knows already is refused:
    /// MatDock's call would change that environment instead.
    /// </summary>
    public async Task<NodeWrite> AddAsync(string name, string host, int port, string user, CancellationToken ct = default)
    {
        if ((await ListAsync(ct, fresh: true)).Any(n => string.Equals(n.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A node named \"{name.Trim()}\" exists already. Choose another name.");
        }

        var answer = await _matDock.PutSshEnvironmentAsync(name.Trim(), host.Trim(), port, user.Trim(), newKey: false, ct);
        Forget();
        return WriteOf(answer);
    }

    /// <summary>A new key pair for a node (the old key stays valid on the VM until it is removed there); answers the new public key.</summary>
    public async Task<NodeWrite> NewKeyAsync(long id, CancellationToken ct = default)
    {
        var node = await RequireOtherAsync(id, ct);
        var answer = await _matDock.PutSshEnvironmentAsync(node.Name, node.Host!, node.Port ?? 22, node.User ?? "root", newKey: true, ct);
        Forget();
        return WriteOf(answer);
    }

    /// <summary>Connects to the node again; answers whether Docker answered and what MatDock said.</summary>
    public async Task<(bool Connected, string Message)> TestAsync(long id, CancellationToken ct = default)
    {
        await RequireOtherAsync(id, ct);
        var answer = await _matDock.TestEnvironmentAsync(id, ct);
        Forget();
        var connected = answer["connected"] is JsonValue value && value.TryGetValue<bool>(out var ok) && ok;
        var message = Text(answer, "message");
        return (connected, connected
            ? $"Connected: Docker {Text(answer, "dockerVersion") ?? "?"} answers."
            : message ?? "MatDock could not connect to the node.");
    }

    /// <summary>
    /// Removes a node MatOS added. Refused while an app of MatOS runs there (move or remove it first); MatDock refuses too while it has stacks or schedules there.
    /// </summary>
    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        var node = await RequireOtherAsync(id, ct);
        if (!node.IsOwn)
        {
            throw new InvalidOperationException($"{node.Name} was set up in MatDock, not in MatOS: remove it there.");
        }

        var apps = await _db.InstalledApps.AsNoTracking().Where(a => a.NodeId == id).Select(a => a.Name).ToListAsync(ct);
        if (apps.Count > 0)
        {
            throw new InvalidOperationException($"These apps run on {node.Name}: {string.Join(", ", apps.Order(StringComparer.OrdinalIgnoreCase))}. Move or remove them first.");
        }

        await _matDock.DeleteEnvironmentAsync(id, ct);
        Forget();
    }

    /// <summary>Forgets the cached list, so the next page asks MatDock again.</summary>
    public void Forget() => _cache.Remove(CacheKey);

    /// <summary>The line to run on the VM that puts the public key into the authorized keys of the user.</summary>
    public static string InstallLine(string publicKey)
        => $"mkdir -p ~/.ssh && echo '{publicKey.Trim()}' >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";

    private async Task<NodeInfo> RequireOtherAsync(long id, CancellationToken ct)
        => (await ListAsync(ct, fresh: true)).FirstOrDefault(n => n.Id == id && !n.IsLocal)
           ?? throw new InvalidOperationException("MatDock does not know this node (any more).");

    private static NodeWrite WriteOf(JsonObject answer)
        => new((long)(Number(answer, "id") ?? 0), Text(answer, "name") ?? string.Empty, Text(answer, "publicKey"),
            answer["connected"] is JsonValue value && value.TryGetValue<bool>(out var ok) && ok, Text(answer, "message"));

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static double? Number(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
