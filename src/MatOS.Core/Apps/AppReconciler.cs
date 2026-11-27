using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Core.Apps;

/// <summary>
/// Takes the stacks that MatDock has for MatOS (<c>managedBy: matos</c>) and that MatOS does not know back as apps. That is
/// what happens after a backup was restored in MatDock, or when MatOS's database was lost: the stack still carries everything
/// (the catalog block, the compose, the variables), so nothing has to be entered again.
/// </summary>
public sealed class AppReconciler
{
    private readonly MatOSDbContext _db;
    private readonly MatDockClient _matdock;
    private readonly MatDockEngine _engine;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly string _prefix;

    public AppReconciler(
        MatOSDbContext db,
        MatDockClient matdock,
        MatDockEngine engine,
        CatalogService catalog,
        ISecretProtector protector,
        IOptions<MatOSOptions> options,
        MatOS.Core.Nodes.NodeService nodes)
    {
        _nodes = nodes;
        _db = db;
        _matdock = matdock;
        _engine = engine;
        _catalog = catalog;
        _protector = protector;
        _prefix = options.Value.ProjectPrefix + "-";
    }

    private readonly MatOS.Core.Nodes.NodeService _nodes;

    /// <summary>Returns how many apps were taken over (of this machine and of every node that is online: docs/ARCHITECTURE.md "Nodes").</summary>
    public async Task<int> ReconcileAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!await _engine.IsAvailableAsync(ct))
        {
            return 0;
        }

        var known = (await _db.InstalledApps.Select(a => a.Slug).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var adopted = 0;
        var places = new List<(long? Node, string? Address)> { (null, null) };
        places.AddRange((await _nodes.ListAsync(ct)).Where(n => !n.IsLocal && n.IsOnline && n.Host is not null).Select(n => ((long?)n.Id, n.Host)));
        foreach (var (node, address) in places)
        {
            foreach (var name in await _matdock.ListManagedStackNamesAsync(ct, node))
            {
                if (!name.StartsWith(_prefix, StringComparison.Ordinal) || known.Contains(name[_prefix.Length..]))
                {
                    continue;
                }

                var slug = name[_prefix.Length..];
                var spec = await _matdock.GetStackAsync(name, slug, ct, node);
                if (spec is null || !TryAdopt(spec, out var app, out var variables, out var catalog))
                {
                    log($"The stack '{name}' in MatDock carries no MatOS app definition and is left alone.");
                    continue;
                }

                app.NodeId = node;
                app.NodeAddress = address;
                known.Add(slug);

                app.VariablesJson = AppVariables.Serialize(catalog, variables, _protector);
                app.CredentialsJson = AppCredentials.Serialize(AppCredentials.Build(catalog, variables), _protector);
                app.SortOrder = known.Count + adopted;
                _db.InstalledApps.Add(app);
                adopted++;
                log($"Took {app.Name} over from MatDock{(node is null ? string.Empty : $" (on node {address})")}.");
            }
        }

        if (adopted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return adopted;
    }

    private bool TryAdopt(StackSpec spec, out InstalledApp app, out Dictionary<string, string> variables, out CatalogApp catalog)
    {
        app = null!;
        catalog = null!;
        variables = EnvFileParser.Parse(spec.Env).Where(kv => !EnvFileParser.IsBuiltIn(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        try
        {
            // The stack's own x-matos block describes the app; the live catalog entry (if it still exists) wins for the questions.
            var fromStack = CatalogParser.Parse(spec.Compose, spec.Name);
            catalog = _catalog.Get(fromStack.Id) ?? fromStack;
            app = new InstalledApp
            {
                Slug = spec.Slug,
                CatalogId = catalog.Id,
                Name = ReadName(spec.Compose) ?? catalog.Name,
                Role = catalog.Role,
                IsCore = catalog.Core,
                ProjectName = spec.Name,
                UiService = catalog.Ui?.Service,
                UiPort = catalog.Ui?.Port,
                UiPath = catalog.Ui?.Path,
                HostPort = catalog.Ui is null ? null : ReadHostPort(spec.Compose, catalog.Ui.Service, catalog.Ui.Port),
                Lifecycle = AppLifecycle.Installed,
                Engine = "matdock",
                ComposeSha = ComposeHash.Of(spec.Compose),
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The display name MatOS wrote into the <c>x-matdock</c> block (the user may have renamed the app).</summary>
    private static string? ReadName(string compose)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(compose);
        return root.TryGetValue("x-matdock", out var block) && block is IDictionary<object, object> map && map.TryGetValue("name", out var name)
            ? name?.ToString()
            : null;
    }

    private static int? ReadHostPort(string compose, string service, int containerPort)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(compose);
        if (!root.TryGetValue("services", out var servicesNode) || servicesNode is not IDictionary<object, object> services ||
            !services.TryGetValue(service, out var serviceNode) || serviceNode is not IDictionary<object, object> svc ||
            !svc.TryGetValue("ports", out var portsNode) || portsNode is not IList<object> ports)
        {
            return null;
        }

        foreach (var port in ports.Select(p => p?.ToString() ?? string.Empty))
        {
            var parts = port.Split(':');
            if (parts.Length >= 2 && parts[^1].Split('/')[0] == containerPort.ToString() && int.TryParse(parts[^2], out var host))
            {
                return host;
            }
        }

        return null;
    }
}
