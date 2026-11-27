using System.Text.Json.Nodes;
using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Nodes;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

/// <summary>
/// Nodes (docs/ARCHITECTURE.md "Nodes"): MatDock's environments as MatOS reads them, the form to add one, the calls on a node, an app rendered for another
/// node (no shared network, its own port), its internal address, the bindings it can use from there, the copies of a schedule for all apps, and what may move.
/// </summary>
public class NodeTests
{
    private const string Environments = """
        [
          { "id": 7, "name": "vm2", "type": "ssh", "host": "192.168.1.22", "port": 22, "username": "docker", "status": "offline", "enabled": true,
            "managedBy": "matos", "publicKey": "ssh-rsa AAAA matdock@192.168.1.22", "message": "Permission denied (publickey)." },
          { "id": 1, "name": "local", "type": "local", "host": null, "status": "online", "enabled": true, "dockerVersion": "29.1",
            "reading": { "cpuPercent": 12.5, "memoryPercent": 40, "diskPercent": 61, "containersRunning": 9 } },
          { "id": 5, "name": "Alpha", "type": "ssh", "host": "192.168.1.21", "port": 2222, "username": "ops", "status": "online", "enabled": true,
            "managedBy": null, "dockerVersion": "28.0" }
        ]
        """;

    [Fact]
    public void The_nodes_are_MatDocks_environments_this_machine_first()
    {
        var nodes = NodeService.FromEnvironments(JsonNode.Parse(Environments) as JsonArray);

        Assert.Equal(["local", "Alpha", "vm2"], nodes.Select(n => n.Name));
        var local = nodes[0];
        Assert.True(local.IsLocal);
        Assert.Null(local.AppNodeId);
        Assert.Equal(NodeService.ThisMachine, local.DisplayName);
        Assert.Equal(new NodeLoad(12.5, 40, 61), local.Load);
        Assert.Equal(9, local.ContainersRunning);

        var alpha = nodes[1];
        Assert.Equal((5L, "192.168.1.21", 2222, "ops"), (alpha.AppNodeId!.Value, alpha.Host!, alpha.Port!.Value, alpha.User!));
        Assert.True(alpha.IsOnline);
        Assert.False(alpha.IsOwn);

        var vm2 = nodes[2];
        Assert.True(vm2.IsOwn);
        Assert.False(vm2.IsOnline);
        Assert.Equal("Offline", vm2.StatusText);
        Assert.StartsWith("ssh-rsa ", vm2.PublicKey);
        Assert.Contains("publickey", vm2.Message);

        Assert.Empty(NodeService.FromEnvironments(null));
    }

    [Fact]
    public void The_form_to_add_a_node_wants_a_name_an_address_a_port_and_a_user()
    {
        Assert.Empty(NodeService.Validate("vm1", "192.168.1.21", 22, "docker"));
        Assert.Empty(NodeService.Validate("Docker host 2", "docker2.lan", 2222, "root"));

        Assert.Single(NodeService.Validate("", "192.168.1.21", 22, "docker"));
        Assert.Single(NodeService.Validate("This machine", "192.168.1.21", 22, "docker"));
        Assert.Single(NodeService.Validate("local", "192.168.1.21", 22, "docker"));
        Assert.Single(NodeService.Validate("vm1", "http://192.168.1.21", 22, "docker"));
        Assert.Single(NodeService.Validate("vm1", "a b", 22, "docker"));
        Assert.Single(NodeService.Validate("vm1", "vm1.lan", 0, "docker"));
        Assert.Single(NodeService.Validate("vm1", "vm1.lan", 22, "no body"));
        Assert.Equal(4, NodeService.Validate(null, null, 70000, null).Count);
    }

    [Fact]
    public void The_line_for_the_VM_adds_the_key_to_the_authorized_keys()
    {
        var line = NodeService.InstallLine("ssh-rsa AAAAB3Nza matdock@vm1\n");

        Assert.Equal("mkdir -p ~/.ssh && echo 'ssh-rsa AAAAB3Nza matdock@vm1' >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys", line);
    }

    [Fact]
    public void A_call_on_a_node_names_its_environment()
    {
        Assert.Equal("stacks", MatDockClient.At("stacks", null));
        Assert.Equal("stacks?environment=7", MatDockClient.At("stacks", 7));
        Assert.Equal("containers?maxAge=3&environment=7", MatDockClient.At("containers?maxAge=3", 7));
        Assert.Equal("stacks/matos-wiki?volumes=true&environment=12", MatDockClient.At("stacks/matos-wiki?volumes=true", 12));
    }

    [Fact]
    public void The_ports_MatDock_lists_are_read_like_those_of_docker_ps()
    {
        var ports = DockerService.ParsePorts("matos-wiki-web", "0.0.0.0:8105->80/tcp, [::]:8105->80/tcp, 0.0.0.0:2587->2587/tcp").ToList();

        Assert.Equal([8105, 8105, 2587], ports.Select(p => p.HostPort));
        Assert.All(ports, p => Assert.Equal("matos-wiki-web", p.Container));
        Assert.Empty(DockerService.ParsePorts("x", null));
        Assert.Empty(DockerService.ParsePorts("x", "80/tcp"));
    }

    // ------------------------------------------------------------------ an app on another node

    private static readonly IOptions<MatOSOptions> Options = Microsoft.Extensions.Options.Options.Create(new MatOSOptions());

    private static CatalogApp Entry(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return CatalogParser.Parse(reader.ReadToEnd(), id + ".yml");
    }

    private static Dictionary<string, object> Parse(string yaml) => new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

    private static IDictionary<object, object> Service(Dictionary<string, object> compose, string name)
        => (IDictionary<object, object>)((IDictionary<object, object>)compose["services"])[name];

    private static InstalledApp App(long? node = null, string? address = null)
        => new() { Slug = "matdo", Name = "MatDo", ProjectName = "matos-matdo", UiService = "web", UiPort = 6006, HostPort = 8105, NodeId = node, NodeAddress = address };

    [Fact]
    public void An_app_on_another_node_joins_no_shared_network_and_publishes_its_own_port()
    {
        var renderer = new ComposeRenderer(Options);

        var here = Parse(renderer.Render(Entry("matdo"), App(), ports: new ComposePorts(false, [])));
        Assert.Contains("matos", ((IDictionary<object, object>)here["networks"]).Keys.Select(k => k.ToString()));
        Assert.False(Service(here, "web").ContainsKey("ports"));

        // Routed by the proxy on this machine or not: on the node its port is published, because that is where the proxy reaches it.
        var there = renderer.Render(Entry("matdo"), App(7, "192.168.1.22"), ports: new ComposePorts(false, []));
        var compose = Parse(there);
        Assert.Contains("8105:6006", ((IEnumerable<object>)Service(compose, "web")["ports"]).Select(p => p.ToString()));
        Assert.False(compose.TryGetValue("networks", out var networks) && ((IDictionary<object, object>)networks).ContainsKey("matos"));
        Assert.DoesNotContain("external", there, StringComparison.Ordinal);
        Assert.All(((IDictionary<object, object>)compose["services"]).Values.Cast<IDictionary<object, object>>(),
            s => Assert.False(s.TryGetValue("networks", out var n) && n.ToString()!.Contains("matos", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_internal_address_of_an_app_on_another_node_is_the_node_and_its_port()
    {
        Assert.Equal("http://matos-matdo-web:6006", AppAddresses.InternalUrlOf(App(), "matos-matdo-web"));
        Assert.Equal("http://192.168.1.22:8105", AppAddresses.InternalUrlOf(App(7, "192.168.1.22"), "matos-matdo-web"));
        Assert.Equal("http://[fd00::22]:8105", AppAddresses.InternalUrlOf(App(7, "fd00::22"), "matos-matdo-web"));
        // On a node without a published port there is no way in.
        Assert.Null(AppAddresses.InternalUrlOf(new InstalledApp { Slug = "matdo", UiService = "web", UiPort = 6006, NodeId = 7, NodeAddress = "192.168.1.22" }, "matos-matdo-web"));
    }

    [Fact]
    public void A_binding_for_another_node_uses_the_public_address_of_the_provider_or_is_refused()
    {
        var identity = new Dictionary<string, string>
        {
            ["MATIDENTITY_AUTHORITY"] = "https://id.example.com",
            ["MATIDENTITY_INTERNAL_URL"] = "http://matos-matidentity-matidentity:8080",
            ["MATIDENTITY_CLIENT_ID"] = "matdo",
        };
        var reachable = BindingBroker.ForNode(identity, "http://matos-matidentity-matidentity:8080/", "matos-matidentity-", "https://id.example.com/");
        Assert.NotNull(reachable);
        Assert.Equal("https://id.example.com", reachable["MATIDENTITY_INTERNAL_URL"]);
        Assert.Equal("matdo", reachable["MATIDENTITY_CLIENT_ID"]);

        // Without a public address of the provider the node cannot reach it.
        Assert.Null(BindingBroker.ForNode(identity, "http://matos-matidentity-matidentity:8080", "matos-matidentity-", null));

        // The relay of MatSMTP is a container of the shared network: no binding from another node.
        var mail = new Dictionary<string, string> { ["MATSMTP_HOST"] = "matos-matsmtp-matsmtp", ["MATSMTP_PORT"] = "2025" };
        Assert.Null(BindingBroker.ForNode(mail, "http://matos-matsmtp-matsmtp:2525", "matos-matsmtp-", "https://mail.example.com"));
    }

    [Fact]
    public void A_schedule_for_all_apps_has_a_copy_per_node_that_the_list_leaves_out()
    {
        Assert.Equal("nightly-node7", MatDockBackupProvider.NodeCopyName("nightly", 7));
        Assert.Equal(("nightly", 7L), MatDockBackupProvider.NodeCopyOf(new JsonObject { ["name"] = "nightly-node7", ["stack"] = null, ["managedBy"] = "matos" }));

        // Not a copy: a schedule of one app, one of another tool, an ordinary name.
        Assert.Null(MatDockBackupProvider.NodeCopyOf(new JsonObject { ["name"] = "nightly-node7", ["stack"] = "matos-wiki", ["managedBy"] = "matos" }));
        Assert.Null(MatDockBackupProvider.NodeCopyOf(new JsonObject { ["name"] = "nightly-node7", ["stack"] = null, ["managedBy"] = "someone" }));
        Assert.Null(MatDockBackupProvider.NodeCopyOf(new JsonObject { ["name"] = "nightly", ["stack"] = null, ["managedBy"] = "matos" }));
    }

    [Fact]
    public void Only_an_installed_app_that_MatDock_runs_can_move_and_never_an_engine()
    {
        var app = new InstalledApp { Name = "Wiki", Engine = "matdock", Lifecycle = AppLifecycle.Installed };
        Assert.Null(AppService.CannotMove(app));

        Assert.NotNull(AppService.CannotMove(new InstalledApp { Name = "Matcad", Engine = "matdock", Lifecycle = AppLifecycle.Installed, IsCore = true }));
        Assert.NotNull(AppService.CannotMove(new InstalledApp { Name = "Wiki", Engine = "local", Lifecycle = AppLifecycle.Installed }));
        Assert.NotNull(AppService.CannotMove(new InstalledApp { Name = "Wiki", Engine = "matdock", Lifecycle = AppLifecycle.Failed }));
    }
}
