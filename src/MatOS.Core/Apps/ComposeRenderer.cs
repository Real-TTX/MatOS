using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MatOS.Core.Apps;

/// <summary>
/// Turns a catalog entry into the compose file that is actually deployed: the entry's own services plus what only MatOS
/// knows (container names, labels, the shared network, the published port of the UI) and an <c>x-matdock</c> block so that
/// MatDock's launchpad shows the stack as an app. It is one self-contained file, because the engines (MatDock) keep exactly one
/// compose text per stack, and it is also the file that <c>docker compose</c> runs when MatOS deploys it itself.
/// </summary>
public sealed partial class ComposeRenderer
{
    private readonly MatOSOptions _options;

    public ComposeRenderer(IOptions<MatOSOptions> options)
    {
        _options = options.Value;
    }

    public string ProjectName(string slug) => $"{_options.ProjectPrefix}-{slug}";

    public string ContainerName(string slug, string service) => $"{_options.ProjectPrefix}-{slug}-{service}";

    /// <summary>Name by which other containers on the shared network reach the UI of an app.</summary>
    public string UiHost(InstalledApp app) => ContainerName(app.Slug, app.UiService ?? string.Empty);

    /// <param name="catalog">The catalog entry.</param>
    /// <param name="app">The installed app.</param>
    /// <param name="injected">
    /// Variables the broker gives the app (its bindings, its addresses): passed to its web service as <c>NAME: ${NAME:-}</c>; the values are in the <c>.env</c>.
    /// </param>
    /// <param name="ports">
    /// Who publishes what (the broker decides): an app the reverse proxy routes publishes no port of its own; the proxy publishes the ports of its port routes
    /// on its route service. Null: the app publishes its own port, as without a proxy.
    /// </param>
    public string Render(CatalogApp catalog, InstalledApp app, IEnumerable<string>? injected = null, ComposePorts? ports = null)
    {
        var names = injected?.ToList() ?? [];
        var stream = new YamlStream();
        using (var reader = new StringReader(catalog.ComposeYaml))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];

        foreach (var (keyNode, valueNode) in services.Children)
        {
            var service = (YamlMappingNode)valueNode;
            var name = ((YamlScalarNode)keyNode).Value!;
            var isUi = catalog.Ui?.Service == name;
            var shared = isUi || catalog.Shared.Contains(name);

            SetScalar(service, "container_name", ContainerName(app.Slug, name), ScalarStyle.Plain);
            MergeLabels(service, new Dictionary<string, string>
            {
                [DockerService.ManagedLabel] = "true",
                [DockerService.AppLabel] = catalog.Id,
                [DockerService.InstanceLabel] = app.Slug,
                [DockerService.ServiceLabel] = name,
            });

            if (shared)
            {
                MergeNetworks(service, ["default", _options.NetworkName]);
            }

            if (isUi && names.Count > 0)
            {
                MergeEnvironment(service, names);
            }

            if (isUi && app.HostPort is { } hostPort && catalog.Ui is { } ui && ports?.OwnPort != false)
            {
                AppendToSequence(service, "ports", $"{hostPort}:{ui.Port}");
            }

            if (catalog.Routes?.Service == name)
            {
                foreach (var port in ports?.RoutePorts ?? [])
                {
                    AppendToSequence(service, "ports", $"{port}:{port}");
                }
            }
        }

        AddSharedNetwork(root);
        RenameCatalogBlock(root);
        SetMapping(root, "x-matdock", new Dictionary<string, string>
        {
            ["name"] = app.Name,
            ["description"] = catalog.Tagline,
            ["category"] = catalog.Category,
        });

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        // Compose reads the file on Linux: always LF. The YAML writer ends a document with "..."; drop it.
        var text = writer.ToString().ReplaceLineEndings("\n").TrimEnd();
        if (text.EndsWith("\n...", StringComparison.Ordinal))
        {
            text = text[..^4].TrimEnd();
        }

        return text + "\n";
    }

    /// <summary>The host ports the catalog entry itself publishes on a service (<c>"80:80"</c>, <c>"443:443/udp"</c>; a port from a variable, <c>${X:-2587}:2587</c>, by its default).</summary>
    public static IReadOnlySet<int> DeclaredHostPorts(CatalogApp catalog, string service)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(catalog.ComposeYaml))
        {
            stream.Load(reader);
        }

        var result = new HashSet<int>();
        if (stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode) || servicesNode is not YamlMappingNode services
            || !services.Children.TryGetValue(new YamlScalarNode(service), out var serviceNode) || serviceNode is not YamlMappingNode map
            || !map.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode) || portsNode is not YamlSequenceNode ports)
        {
            return result;
        }

        foreach (var entry in ports.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? string.Empty))
        {
            // host:container[/proto], or ip:host:container; a variable with a default stands for its default
            var text = DefaultOfVariable().Replace(entry, m => m.Groups["default"].Value);
            var parts = text.Split('/')[0].Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[^2], out var host))
            {
                result.Add(host);
            }
        }

        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{[A-Za-z0-9_]+:-(?<default>[^}]*)\}")]
    private static partial System.Text.RegularExpressions.Regex DefaultOfVariable();

    // ------------------------------------------------------------------ YAML helpers

    private static void SetScalar(YamlMappingNode map, string key, string value, ScalarStyle style)
        => map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value) { Style = style };

    private static void MergeLabels(YamlMappingNode service, IReadOnlyDictionary<string, string> labels)
    {
        var key = new YamlScalarNode("labels");
        if (service.Children.TryGetValue(key, out var existing) && existing is YamlSequenceNode sequence)
        {
            // The list form ("key=value") stays a list.
            foreach (var (name, value) in labels)
            {
                sequence.Add(new YamlScalarNode($"{name}={value}") { Style = ScalarStyle.DoubleQuoted });
            }

            return;
        }

        var map = existing as YamlMappingNode ?? new YamlMappingNode();
        foreach (var (name, value) in labels)
        {
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }

        service.Children[key] = map;
    }

    /// <summary>Adds <c>NAME: ${NAME:-}</c> for every name the service does not set itself (a name the catalog entry passes stays as it is written).</summary>
    private static void MergeEnvironment(YamlMappingNode service, IEnumerable<string> names)
    {
        var key = new YamlScalarNode("environment");
        service.Children.TryGetValue(key, out var existing);
        if (existing is YamlSequenceNode list)
        {
            var present = list.Children.OfType<YamlScalarNode>().Select(s => s.Value?.Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);
            foreach (var name in names.Where(n => !present.Contains(n)))
            {
                list.Add(new YamlScalarNode(name + "=${" + name + ":-}") { Style = ScalarStyle.DoubleQuoted });
            }

            return;
        }

        var map = existing as YamlMappingNode ?? new YamlMappingNode();
        foreach (var name in names.Where(n => !map.Children.ContainsKey(new YamlScalarNode(n))))
        {
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode("${" + name + ":-}") { Style = ScalarStyle.DoubleQuoted };
        }

        service.Children[key] = map;
    }

    private static void MergeNetworks(YamlMappingNode service, IReadOnlyList<string> networks)
    {
        var key = new YamlScalarNode("networks");
        if (service.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode map)
        {
            foreach (var network in networks.Where(n => !map.Children.ContainsKey(new YamlScalarNode(n))))
            {
                map.Children[new YamlScalarNode(network)] = new YamlMappingNode();
            }

            return;
        }

        var sequence = existing as YamlSequenceNode ?? new YamlSequenceNode();
        var present = sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).ToHashSet();
        foreach (var network in networks.Where(n => !present.Contains(n)))
        {
            sequence.Add(new YamlScalarNode(network));
        }

        service.Children[key] = sequence;
    }

    private static void AppendToSequence(YamlMappingNode service, string key, string value)
    {
        var node = new YamlScalarNode(key);
        var sequence = service.Children.TryGetValue(node, out var existing) && existing is YamlSequenceNode s ? s : new YamlSequenceNode();
        sequence.Add(new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted });
        service.Children[node] = sequence;
    }

    private void AddSharedNetwork(YamlMappingNode root)
    {
        var key = new YamlScalarNode("networks");
        var networks = root.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode m ? m : new YamlMappingNode();
        networks.Children[new YamlScalarNode(_options.NetworkName)] = new YamlMappingNode
        {
            { "name", _options.NetworkName },
            { "external", new YamlScalarNode("true") },
        };
        root.Children[key] = networks;
    }

    /// <summary>
    /// The catalog block of the written file is always <c>x-matos</c>: a catalog copy of Mattiverse (<c>x-mattiverse</c>) gets it renamed in its place, and
    /// with both blocks the old one is dropped (<c>x-matos</c> wins, as in <see cref="CatalogParser"/>).
    /// </summary>
    private static void RenameCatalogBlock(YamlMappingNode root)
    {
        var legacy = new YamlScalarNode(Mattiverse.CatalogBlock);
        if (!root.Children.ContainsKey(legacy))
        {
            return;
        }

        var current = new YamlScalarNode(CatalogParser.Block);
        var hasCurrent = root.Children.ContainsKey(current);
        var children = root.Children.ToList();
        root.Children.Clear();
        foreach (var (key, value) in children)
        {
            if (key.Equals(legacy))
            {
                if (!hasCurrent)
                {
                    root.Children.Add(current, value);
                }
            }
            else
            {
                root.Children.Add(key, value);
            }
        }
    }

    private static void SetMapping(YamlMappingNode root, string key, IReadOnlyDictionary<string, string> values)
    {
        var map = new YamlMappingNode();
        foreach (var (name, value) in values.Where(v => !string.IsNullOrWhiteSpace(v.Value)))
        {
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }

        root.Children[new YamlScalarNode(key)] = map;
    }
}

/// <summary>
/// Who publishes which host port of an app: <see cref="OwnPort"/> false when the reverse proxy routes the app (its web service publishes no port), and the
/// ports of the port routes a proxy serves (published on its route service, <c>routes.service</c> of its catalog entry).
/// </summary>
public sealed record ComposePorts(bool OwnPort, IReadOnlyList<int> RoutePorts)
{
    /// <summary>An app without a proxy: its own port, no routes.</summary>
    public static readonly ComposePorts Own = new(true, []);
}
