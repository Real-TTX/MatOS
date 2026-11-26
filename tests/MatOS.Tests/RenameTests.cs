using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatOS.Core;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

/// <summary>
/// MatOS was called Mattiverse until 2026-11 (docs/ARCHITECTURE.md "Renamed from Mattiverse"): everything an install of then persisted or sent over the
/// wire under the old name is still accepted, and what MatOS writes carries the new one.
/// </summary>
public class RenameTests
{
    private static string NewDataPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "matos-rename-" + Guid.NewGuid().ToString("N"));
        new AppPaths(path).EnsureCreated();
        return path;
    }

    private static IConfiguration Config(Dictionary<string, string?>? values = null)
        => new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();

    // ------------------------------------------------------------------ 1. Docker labels

    [Fact]
    public void Containers_are_listed_by_the_labels_of_MatOS_and_of_Mattiverse()
    {
        Assert.Equal(["matos.managed", "mattiverse.managed"], DockerService.AllLabels.Select(l => l.Managed));

        var legacy = DockerService.ListArguments(DockerService.LegacyLabels, "matdo");
        Assert.Contains("label=mattiverse.managed=true", legacy);
        Assert.Contains("label=mattiverse.instance=matdo", legacy);
        // The instance and the service are read from the labels the container carries.
        Assert.Contains("{{.Label \"mattiverse.instance\"}}\t{{.Label \"mattiverse.service\"}}", legacy.Last(), StringComparison.Ordinal);

        var current = DockerService.ListArguments(DockerService.Labels, null);
        Assert.Contains("label=matos.managed=true", current);
        Assert.DoesNotContain(current, a => a.StartsWith("label=matos.instance", StringComparison.Ordinal));
    }

    [Fact]
    public void The_answers_for_both_labels_are_merged_and_a_container_with_both_counts_once()
    {
        var current = "a1\tmatos-matdo-web\tmatdo\trunning\tUp\tmatdo\tweb\tmatos-matdo\t\n";
        var legacy = "b1\tmattiverse-matcad-caddy\tcaddy\trunning\tUp\tmatcad\tcaddy\tmattiverse-matcad\t\na1\tmatos-matdo-web\tmatdo\trunning\tUp\tmatdo\tweb\tmatos-matdo\t\n";

        var containers = DockerService.MergeContainers([current, legacy]);

        Assert.Equal(["a1", "b1"], containers.Select(c => c.Id));
        Assert.Equal("matcad", containers[1].Instance);
    }

    [Fact]
    public void New_containers_get_the_labels_of_MatOS()
    {
        var renderer = new ComposeRenderer(Options.Create(new MatOSOptions()));
        var catalog = CatalogParser.Parse(LegacyEntry, "legacy.yml");
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(renderer.Render(catalog, App("demo")));
        var labels = (IDictionary<object, object>)((IDictionary<object, object>)((IDictionary<object, object>)compose["services"])["web"])["labels"];

        Assert.Equal("true", labels["matos.managed"]);
        Assert.Equal("demo", labels["matos.instance"]);
        Assert.DoesNotContain(labels.Keys, k => k.ToString()!.StartsWith("mattiverse.", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 2. The catalog block

    private const string LegacyEntry = """
        x-mattiverse:
          id: legacy
          name: Legacy
          tagline: From Mattiverse
          ui: { service: web, port: 80 }
        services:
          web:
            image: nginx
        """;

    private static InstalledApp App(string slug) => new() { Slug = slug, Name = slug, ProjectName = "matos-" + slug, UiService = "web", UiPort = 80, HostPort = 8180 };

    [Fact]
    public void A_catalog_file_of_Mattiverse_is_read()
    {
        var app = CatalogParser.Parse(LegacyEntry, "legacy.yml");
        Assert.Equal("legacy", app.Id);
        Assert.Equal("web", app.Ui!.Service);
    }

    [Fact]
    public void With_both_blocks_x_matos_wins()
    {
        var app = CatalogParser.Parse("""
            x-matos:
              id: current
              name: Current
            x-mattiverse:
              id: legacy
              name: Legacy
            services:
              web:
                image: nginx
            """, "both.yml");
        Assert.Equal("current", app.Id);
    }

    [Fact]
    public void A_missing_block_names_x_matos()
    {
        var error = Assert.Throws<FormatException>(() => CatalogParser.Parse("services:\n  a:\n    image: x\n", "none.yml"));
        Assert.Contains("'x-matos'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_written_compose_file_carries_x_matos_in_the_place_of_x_mattiverse()
    {
        var renderer = new ComposeRenderer(Options.Create(new MatOSOptions()));
        var text = renderer.Render(CatalogParser.Parse(LegacyEntry, "legacy.yml"), App("demo"));
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(text);

        Assert.False(compose.ContainsKey("x-mattiverse"));
        Assert.Equal("legacy", ((IDictionary<object, object>)compose["x-matos"])["id"]);
        // In its place: still the first key of the file.
        Assert.Equal("x-matos", compose.Keys.First());
        // And what was written reads back (MatDock's copy of the stack, AppReconciler).
        Assert.Equal("legacy", CatalogParser.Parse(text, "stack").Id);
    }

    [Fact]
    public void With_both_blocks_the_written_file_keeps_only_x_matos()
    {
        var renderer = new ComposeRenderer(Options.Create(new MatOSOptions()));
        var entry = CatalogParser.Parse("""
            x-matos:
              id: current
              name: Current
              ui: { service: web, port: 80 }
            x-mattiverse:
              id: legacy
              name: Legacy
            services:
              web:
                image: nginx
            """, "both.yml");
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(renderer.Render(entry, App("demo")));

        Assert.False(compose.ContainsKey("x-mattiverse"));
        Assert.Equal("current", ((IDictionary<object, object>)compose["x-matos"])["id"]);
    }

    [Fact]
    public void A_catalog_copy_of_Mattiverse_in_the_data_folder_is_loaded()
    {
        var paths = new AppPaths(NewDataPath());
        File.WriteAllText(Path.Combine(paths.CatalogPath, "legacy.yml"), LegacyEntry);

        var catalog = new CatalogService(paths, NullLogger<CatalogService>.Instance);

        Assert.Equal("Legacy", catalog.Get("legacy")!.Name);
    }

    // ------------------------------------------------------------------ 3. The database file

    [Fact]
    public void The_database_of_Mattiverse_moves_to_matos_db_with_its_sidecars()
    {
        var paths = new AppPaths(NewDataPath());
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.WriteAllText(Path.Combine(paths.DataPath, "mattiverse.db" + suffix), "content" + suffix);
        }

        var log = new List<string>();
        Assert.True(RenameUpgrade.MoveDatabase(paths, log));

        Assert.Equal(Path.Combine(paths.DataPath, "matos.db"), paths.DatabasePath);
        Assert.Equal("content", File.ReadAllText(paths.DatabasePath));
        Assert.Equal("content-wal", File.ReadAllText(paths.DatabasePath + "-wal"));
        Assert.Equal("content-shm", File.ReadAllText(paths.DatabasePath + "-shm"));
        Assert.False(File.Exists(Path.Combine(paths.DataPath, "mattiverse.db")));
        Assert.Single(log);
    }

    [Fact]
    public void An_existing_matos_db_is_used_and_the_old_one_left_alone()
    {
        var paths = new AppPaths(NewDataPath());
        File.WriteAllText(paths.DatabasePath, "new");
        File.WriteAllText(Path.Combine(paths.DataPath, "mattiverse.db"), "old");

        Assert.True(RenameUpgrade.MoveDatabase(paths, []));
        Assert.Equal("new", File.ReadAllText(paths.DatabasePath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(paths.DataPath, "mattiverse.db")));
    }

    [Fact]
    public void An_empty_matos_db_does_not_keep_the_database_of_Mattiverse_out()
    {
        // A SQLite viewer on the same volume creates an empty file at the path it is pointed at before MatOS starts.
        var paths = new AppPaths(NewDataPath());
        File.WriteAllText(paths.DatabasePath, string.Empty);
        File.WriteAllText(Path.Combine(paths.DataPath, "mattiverse.db"), "old");

        Assert.True(RenameUpgrade.MoveDatabase(paths, []));
        Assert.Equal("old", File.ReadAllText(paths.DatabasePath));
        Assert.False(File.Exists(Path.Combine(paths.DataPath, "mattiverse.db")));
    }

    [Fact]
    public void A_new_install_has_no_database_to_move()
        => Assert.False(RenameUpgrade.MoveDatabase(new AppPaths(NewDataPath()), []));

    // ------------------------------------------------------------------ 4. The options

    [Fact]
    public void The_section_Mattiverse_still_binds_and_MatOS_wins()
    {
        var options = MatOSConfiguration.Read(Config(new()
        {
            ["Mattiverse:HostPortRangeStart"] = "9000",
            ["Mattiverse:NetworkSubnet"] = "10.1.0.0/24",
            ["Mattiverse:Admin:Seed"] = "true",
            ["MatOS:NetworkSubnet"] = "10.2.0.0/24",
            ["MatOS:HostPortRangeEnd"] = "9100",
        }));

        Assert.Equal(9000, options.HostPortRangeStart);
        Assert.Equal(9100, options.HostPortRangeEnd);
        Assert.Equal("10.2.0.0/24", options.NetworkSubnet);
        Assert.True(options.Admin.Seed);
    }

    [Fact]
    public void One_option_comes_from_MatOS_else_from_Mattiverse()
    {
        var config = Config(new() { ["Mattiverse:DataPath"] = "/old", ["Mattiverse:ProjectPrefix"] = "mv", ["MatOS:ProjectPrefix"] = "os", ["MatOS:NetworkName"] = "" });

        Assert.Equal("/old", MatOSConfiguration.Value(config, "DataPath"));
        Assert.Equal("os", MatOSConfiguration.Value(config, "ProjectPrefix"));
        Assert.Null(MatOSConfiguration.Value(config, "NetworkName"));
    }

    [Fact]
    public void Every_consumer_gets_the_names_of_the_install()
    {
        var services = new ServiceCollection();
        services.AddMatOSOptions(Config(new() { ["Mattiverse:HostPortRangeStart"] = "9000" }), new RenameUpgradeResult("mattiverse", "mattiverse", []), "/data");
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MatOSOptions>>().Value;

        Assert.Equal("mattiverse", options.NetworkName);
        Assert.Equal("mattiverse", options.ProjectPrefix);
        Assert.Equal("/data", options.DataPath);
        Assert.Equal(9000, options.HostPortRangeStart);
        Assert.Equal("mattiverse-matcad", new ComposeRenderer(Options.Create(options)).ProjectName("matcad"));
    }

    // ------------------------------------------------------------------ 5. Network and project prefix

    private static async Task<SystemConfig> StoredAsync(string data)
        => JsonSerializer.Deserialize<SystemConfig>(await File.ReadAllTextAsync(Path.Combine(data, "config", "system.json")), JsonConfigStore.JsonOptions)!;

    [Fact]
    public async Task A_new_install_is_matos_and_keeps_it()
    {
        var data = NewDataPath();
        var result = await RenameUpgrade.RunAsync(new AppPaths(data), Config());

        Assert.Equal(("matos", "matos"), (result.NetworkName, result.ProjectPrefix));
        var naming = (await StoredAsync(data)).Naming!;
        Assert.Equal(("matos", "matos", "matos"), (naming.NetworkName, naming.ProjectPrefix, naming.IdentityClientId));
    }

    [Fact]
    public async Task An_install_with_the_database_of_Mattiverse_keeps_its_names()
    {
        var data = NewDataPath();
        File.WriteAllText(Path.Combine(data, "mattiverse.db"), string.Empty);

        var result = await RenameUpgrade.RunAsync(new AppPaths(data), Config());

        Assert.Equal(("mattiverse", "mattiverse"), (result.NetworkName, result.ProjectPrefix));
        Assert.True(File.Exists(Path.Combine(data, "matos.db")));
        Assert.Equal("mattiverse", (await StoredAsync(data)).Naming!.ProjectPrefix);
    }

    [Fact]
    public async Task An_install_with_a_system_json_of_Mattiverse_keeps_its_names_and_its_client()
    {
        var data = NewDataPath();
        File.WriteAllText(Path.Combine(data, "config", "system.json"), """
            { "instanceName": "Mattiverse", "baseDomain": "example.com", "identity": { "authority": "https://id.example.com", "clientId": "mattiverse", "origins": [] } }
            """);

        var result = await RenameUpgrade.RunAsync(new AppPaths(data), Config());

        Assert.Equal("mattiverse", result.NetworkName);
        var stored = await StoredAsync(data);
        Assert.Equal("mattiverse", stored.Naming!.IdentityClientId);
        Assert.Equal("mattiverse", stored.SelfClientId);
        Assert.Equal("example.com", stored.BaseDomain);
    }

    [Fact]
    public async Task An_install_with_apps_in_projects_of_Mattiverse_keeps_its_names()
    {
        var data = NewDataPath();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "matos.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"InstalledApp\" (\"Id\" INTEGER PRIMARY KEY, \"ProjectName\" TEXT); INSERT INTO \"InstalledApp\" (\"ProjectName\") VALUES ('mattiverse-matdock');";
            command.ExecuteNonQuery();
        }

        Assert.True(RenameUpgrade.HasLegacyApps(Path.Combine(data, "matos.db")));
        Assert.Equal("mattiverse", (await RenameUpgrade.RunAsync(new AppPaths(data), Config())).ProjectPrefix);
    }

    [Fact]
    public void A_database_without_apps_of_Mattiverse_or_without_the_table_is_no_upgrade()
    {
        var data = NewDataPath();
        var file = Path.Combine(data, "matos.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"Other\" (\"Id\" INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        Assert.False(RenameUpgrade.HasLegacyApps(file));
        Assert.False(RenameUpgrade.HasLegacyApps(Path.Combine(data, "missing.db")));
    }

    [Fact]
    public async Task The_stored_names_count_and_an_explicit_option_wins()
    {
        var data = NewDataPath();
        File.WriteAllText(Path.Combine(data, "config", "system.json"), """
            { "naming": { "networkName": "mattiverse", "projectPrefix": "mattiverse", "identityClientId": "mattiverse" } }
            """);

        Assert.Equal("mattiverse", (await RenameUpgrade.RunAsync(new AppPaths(data), Config())).NetworkName);

        var result = await RenameUpgrade.RunAsync(new AppPaths(data), Config(new() { ["MatOS:NetworkName"] = "mvshots", ["Mattiverse:ProjectPrefix"] = "mvshots" }));
        Assert.Equal(("mvshots", "mvshots"), (result.NetworkName, result.ProjectPrefix));
        // The stored names stay as they are: the option only wins while it is set.
        Assert.Equal("mattiverse", (await StoredAsync(data)).Naming!.NetworkName);
    }

    // ------------------------------------------------------------------ 6. managedBy

    [Fact]
    public void The_mark_of_MatOS_and_of_Mattiverse_are_its_own()
    {
        Assert.Equal("matos", Owner.Name);
        Assert.True(Owner.IsOurs("matos"));
        Assert.True(Owner.IsOurs("mattiverse"));
        Assert.True(Owner.IsOurs("Mattiverse"));
        Assert.False(Owner.IsOurs("manual"));
        Assert.False(Owner.IsOurs(null));
    }

    /// <summary>Records every request; answers by "METHOD path?query" first, then by "METHOD path", else <c>{}</c>.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _answers = new();

        public List<(HttpMethod Method, string Path, string Query, JsonNode? Body)> Requests { get; } = new();

        public Stub Answer(string key, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers[key] = (status, json);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/", string.Empty);
            var query = request.RequestUri.Query;
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, query, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var (status, json) = _answers.TryGetValue($"{request.Method} {path}{query}", out var exact) ? exact
                : _answers.TryGetValue($"{request.Method} {path}", out var byPath) ? byPath
                : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Locator : EngineLocator
    {
        public Locator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://engine.test", "key"));
    }

    [Fact]
    public async Task A_client_Mattiverse_registered_is_deleted_with_its_mark()
    {
        var stub = new Stub()
            .Answer("DELETE clients/matsplit?managedBy=matos", """{"errors":{"managedBy":"This app was not created by this caller."}}""", HttpStatusCode.Conflict)
            .Answer("DELETE clients/matsplit?managedBy=mattiverse", "{}", HttpStatusCode.NoContent);

        await new MatIdentityClient(new Locator(), new Factory(stub)).DeleteClientAsync("matsplit", default);

        Assert.Equal(["?managedBy=matos", "?managedBy=mattiverse"], stub.Requests.Select(r => r.Query));
    }

    [Fact]
    public async Task A_relay_user_of_Mattiverse_is_listed_and_deleted_with_its_mark()
    {
        var stub = new Stub()
            .Answer("GET relay-users", """[{"id":"matbu","managedBy":"mattiverse"},{"id":"printer"},{"id":"matdo","managedBy":"matos"}]""")
            .Answer("DELETE relay-users/matbu?managedBy=matos", """{"error":"managed by mattiverse"}""", HttpStatusCode.Conflict)
            .Answer("DELETE relay-users/matbu?managedBy=mattiverse", "{}", HttpStatusCode.NoContent);
        var client = new MatSmtpClient(new Locator(), new Factory(stub));

        Assert.Equal(["matbu", "matdo"], await client.ListRelayUsersAsync(default));
        await client.DeleteRelayUserAsync("matbu", default);
        Assert.Equal("?managedBy=mattiverse", stub.Requests.Last().Query);
    }

    [Fact]
    public async Task What_MatOS_writes_carries_its_own_mark()
    {
        var stub = new Stub().Answer("PUT relay-users/matbu", """{"id":"matbu","username":"matbu","created":false}""");
        await new MatSmtpClient(new Locator(), new Factory(stub)).UpsertRelayUserAsync("matbu", "MatBu", [], renewSecret: false, default);
        Assert.Equal("matos", stub.Requests[0].Body!["managedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_stacks_of_MatDock_are_asked_for_both_marks()
    {
        var stub = new Stub()
            .Answer("GET stacks?managedBy=matos", """[{"name":"matos-matdo"}]""")
            .Answer("GET stacks?managedBy=mattiverse", """[{"name":"mattiverse-matcad"},{"name":"matos-matdo"}]""");

        var names = await new MatDockClient(new Locator(), new Factory(stub)).ListManagedStackNamesAsync();

        Assert.Equal(["matos-matdo", "mattiverse-matcad"], names);
    }

    [Fact]
    public async Task A_binding_Mattiverse_made_is_removed_with_its_mark()
    {
        var data = NewDataPath();
        var paths = new AppPaths(data);
        var options = Options.Create(new MatOSOptions());
        var catalog = new CatalogService(paths, NullLogger<CatalogService>.Instance);
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var entry = catalog.GetAll().First(a => a.Platform);
        var provider = new InstalledApp
        {
            Slug = entry.Id,
            Name = entry.Name,
            CatalogId = entry.Id,
            ProjectName = "mattiverse-" + entry.Id,
            UiService = "web",
            UiPort = 80,
            HostPort = 8180,
            VariablesJson = AppVariables.Serialize(entry, new Dictionary<string, string> { [ManagedVariables.ApiKey] = "key" }, protector),
        };
        var stub = new Stub()
            .Answer("DELETE /api/bindings/mail.smtp/matbu?managedBy=matos", """{"error":"There is no such binding."}""", HttpStatusCode.NotFound)
            .Answer("DELETE /api/bindings/mail.smtp/matbu?managedBy=mattiverse", "{}", HttpStatusCode.NoContent);
        var platform = new PlatformClient(
            new Factory(stub),
            catalog,
            protector,
            new DockerService(new DockerCli(options, NullLogger<DockerCli>.Instance), options, NullLogger<DockerService>.Instance),
            new ComposeFiles(paths, new ComposeRenderer(options)));

        Assert.True(await platform.UnbindAsync(provider, "mail.smtp", "matbu"));
        Assert.Equal(["?managedBy=matos", "?managedBy=mattiverse"], stub.Requests.Select(r => r.Query));
    }

    // ------------------------------------------------------------------ 7. The client of MatOS in MatIdentity

    [Fact]
    public void The_client_id_is_the_registered_one_else_the_one_of_the_naming()
    {
        Assert.Equal("matos", new SystemConfig().SelfClientId);
        Assert.Equal("mattiverse", new SystemConfig { Naming = InstallNaming.Legacy() }.SelfClientId);
        Assert.Equal("mattiverse", new SystemConfig { Naming = new InstallNaming(), Identity = new IdentitySignIn { ClientId = "mattiverse" } }.SelfClientId);
        Assert.Equal("matos", new SystemConfig { Naming = new InstallNaming(), Identity = new IdentitySignIn() }.SelfClientId);
    }

    [Fact]
    public void The_sign_in_reads_the_stored_client_id()
    {
        var source = File.ReadAllText(Path.Combine(WebUiTests.WebRoot(), "Infrastructure", "MatIdentitySignIn.cs"));
        Assert.DoesNotMatch(new Regex("ClientId = \"mat"), source);
        Assert.Contains("options.ClientId = config.SelfClientId;", source, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 8. The host of MatOS under the domain

    [Fact]
    public void The_host_of_MatOS_is_matos_and_the_one_of_Mattiverse_is_known()
    {
        Assert.Equal("matos.example.com", ProxyPublisher.SelfHost("example.com"));
        Assert.Equal("mattiverse.example.com", ProxyPublisher.FormerSelfHost("example.com."));
    }

    [Fact]
    public async Task The_route_of_Mattiverse_is_removed_like_any_route_MatOS_does_not_want()
    {
        var stub = new Stub().Answer("GET routes/manual", """
            [{"id":1,"host":"matos.example.com","managedBy":"matos"},
             {"id":2,"host":"mattiverse.example.com","managedBy":"mattiverse"},
             {"id":3,"host":"*.example.com","wildcard":true,"managedBy":"mattiverse"},
             {"id":4,"host":"shop.example.com","managedBy":"someone-else"}]
            """);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "matos.example.com", "*.example.com" };

        var removed = await new MatcadClient(new Locator(), new Factory(stub)).RemoveManagedExceptAsync(wanted);

        Assert.Equal(["mattiverse.example.com"], removed);
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path == "routes/2");
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path != "routes/2");
    }

    [Fact]
    public void The_redirect_addresses_follow_the_new_host()
    {
        var stored = new[] { "https://mattiverse.example.com", "http://nas.local:8030" };

        Assert.Equal(
            ["http://nas.local:8030", "https://matos.example.com"],
            IdentityLink.OriginsFor(stored, "example.com", proxyActive: true, origin: null));

        // A person who still comes from the old address (its route is not gone yet) keeps it.
        Assert.Contains("https://mattiverse.example.com", IdentityLink.OriginsFor(stored, "example.com", proxyActive: true, origin: "https://mattiverse.example.com/"));
    }

    [Fact]
    public async Task The_DNS_account_of_Mattiverse_in_Matcad_is_taken_over_and_renamed()
    {
        var stub = new Stub()
            .Answer("GET providers", """[{"id":5,"name":"Mattiverse DNS","type":"cloudflare","managedBy":"mattiverse"}]""")
            .Answer("POST providers", """{"id":5}""");

        var id = await new MatcadClient(new Locator(), new Factory(stub)).UpsertProviderAsync("MatOS DNS", "cloudflare", new Dictionary<string, string>(), "Mattiverse DNS");

        Assert.Equal(5, id);
        var body = stub.Requests.Single(r => r.Method == HttpMethod.Post).Body!;
        Assert.Equal(5, body["id"]!.GetValue<long>());
        Assert.Equal("MatOS DNS", body["name"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_provider_of_the_old_name_that_someone_else_made_is_not_taken()
    {
        var stub = new Stub().Answer("GET providers", """[{"id":5,"name":"Mattiverse DNS","type":"cloudflare"}]""");
        Assert.Null(await new MatcadClient(new Locator(), new Factory(stub)).FindProviderAsync("MatOS DNS", "Mattiverse DNS"));
    }

    [Fact]
    public async Task The_group_of_Mattiverse_in_Matddns_is_renamed_instead_of_doubled()
    {
        var stub = new Stub()
            .Answer("GET domains", """[{"id":"g1","name":"Mattiverse","managedBy":"mattiverse","entries":[{"id":"e-old","hostname":"mattiverse.example.com"},{"id":"e-a","hostname":"a.example.com"}]}]""")
            .Answer("GET rules", """[{"id":"r-old","domainGroupId":"g1","domainEntryId":"e-old"},{"id":"r-a","domainGroupId":"g1","domainEntryId":"e-a"}]""")
            .Answer("POST domains", """{"id":"g1","entries":[{"id":"e-a","hostname":"a.example.com"},{"id":"e-new","hostname":"matos.example.com"}]}""");

        var (groupId, _) = await new MatddnsClient(new Locator(), new Factory(stub)).UpsertGroupAsync(
            "MatOS",
            DnsProviders.Find("cloudflare")!,
            new Dictionary<string, string>(),
            [new DnsRecord("a.example.com", "a", "example.com"), new DnsRecord("matos.example.com", "matos", "example.com")],
            "Mattiverse");

        Assert.Equal("g1", groupId);
        // The rule of the record of the old host goes first, then the group gets its new name and list.
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path == "rules/r-old");
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path == "rules/r-a");
        var body = stub.Requests.Single(r => r.Method == HttpMethod.Post).Body!;
        Assert.Equal("g1", body["id"]!.GetValue<string>());
        Assert.Equal("MatOS", body["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_group_of_Mattiverse_goes_when_there_is_no_domain_any_more()
    {
        var stub = new Stub()
            .Answer("GET domains", """[{"id":"g1","name":"Mattiverse","managedBy":"mattiverse","entries":[]},{"id":"g2","name":"Mine","entries":[]}]""")
            .Answer("GET rules", "[]");

        Assert.True(await new MatddnsClient(new Locator(), new Factory(stub)).DeleteGroupAsync("MatOS", "Mattiverse"));
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path == "domains/g1");
    }

    [Fact]
    public async Task The_source_of_Mattiverse_in_Matddns_is_taken_over()
    {
        var stub = new Stub()
            .Answer("GET sources", """[{"id":"s1","name":"Mattiverse public IP","managedBy":"mattiverse"}]""")
            .Answer("POST sources", """{"id":"s1","entries":[{"id":"se1"}]}""");

        Assert.Equal("se1", await new MatddnsClient(new Locator(), new Factory(stub)).EnsurePublicIpSourceAsync("MatOS public IP", "Mattiverse public IP"));
        var body = stub.Requests.Single(r => r.Method == HttpMethod.Post).Body!;
        Assert.Equal("s1", body["id"]!.GetValue<string>());
        Assert.Equal("MatOS public IP", body["name"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ 9. The places of the windows

    [Fact]
    public void The_home_screen_moves_the_keys_of_Mattiverse_once()
    {
        var script = File.ReadAllText(Path.Combine(WebUiTests.WebRoot(), "wwwroot", "js", "os.js"));
        Assert.Contains("var LEGACY = 'mattiverse.os.';", script, StringComparison.Ordinal);
        Assert.Contains("moveLegacyKeys(function () { return window.localStorage; });", script, StringComparison.Ordinal);
        Assert.Contains("moveLegacyKeys(function () { return window.sessionStorage; });", script, StringComparison.Ordinal);
        // Moved, not copied: the old key goes, so it happens once.
        Assert.Contains("store.removeItem(key);", script, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 10. The instance name

    [Fact]
    public void The_default_name_of_Mattiverse_becomes_MatOS_once_and_a_chosen_one_stays()
    {
        var old = new SystemConfig { InstanceName = "Mattiverse" };
        Assert.True(RenameUpgrade.Apply(old, () => true, null, null, []));
        Assert.Equal("MatOS", old.InstanceName);

        var chosen = new SystemConfig { InstanceName = "Home" };
        Assert.True(RenameUpgrade.Apply(chosen, () => true, null, null, []));
        Assert.Equal("Home", chosen.InstanceName);

        // After the upgrade the owner may call it Mattiverse again: the next start leaves it.
        var again = new SystemConfig { InstanceName = "Mattiverse", Naming = InstallNaming.Legacy() };
        Assert.False(RenameUpgrade.Apply(again, () => throw new InvalidOperationException("not asked"), null, null, []));
        Assert.Equal("Mattiverse", again.InstanceName);
    }

    // ------------------------------------------------------------------ 11. The secrets

    [Fact]
    public void The_secrets_Mattiverse_protected_are_read()
    {
        // The purpose is part of the key derivation: renaming it would make every secret on the volume unreadable.
        Assert.Equal("Mattiverse.Secrets.v1", DataProtectionSecretProtector.Purpose);

        var provider = new EphemeralDataProtectionProvider();
        var stored = provider.CreateProtector("Mattiverse.Secrets.v1").Protect("api-key");
        Assert.Equal("api-key", new DataProtectionSecretProtector(provider).Unprotect(stored));
    }

    // ------------------------------------------------------------------ 12. The engines follow (RenameFollowUp)

    [Fact]
    public void Only_an_install_of_Mattiverse_has_the_rename_to_finish()
    {
        var upgraded = new SystemConfig();
        RenameUpgrade.Apply(upgraded, () => true, null, null, []);
        Assert.True(upgraded.Naming!.FinishRename);

        var fresh = new SystemConfig();
        RenameUpgrade.Apply(fresh, () => false, null, null, []);
        Assert.False(fresh.Naming!.FinishRename);
    }

    [Fact]
    public async Task The_stacks_and_schedules_of_Mattiverse_in_MatDock_get_the_mark_of_MatOS_together()
    {
        var stub = new Stub()
            .Answer("GET stacks?managedBy=mattiverse", """[{"name":"mattiverse-matdrive","managedBy":"mattiverse"},{"name":"other","managedBy":null}]""")
            .Answer("GET stacks/mattiverse-matdrive", """{"name":"mattiverse-matdrive","composeYaml":"services: {}","envContent":"A=1"}""")
            .Answer("GET backup-schedules", """
                [{"name":"Nightly","stack":null,"managedBy":"mattiverse","frequency":"daily","time":"03:00","weekday":null,"retention":7,"stopContainers":true,"enabled":true,"targetId":2},
                 {"name":"Mine","stack":"x","managedBy":null,"frequency":"daily","time":"04:00"},
                 {"name":"New","stack":null,"managedBy":"matos","frequency":"daily","time":"05:00"}]
                """);

        var count = await new MatDockClient(new Locator(), new Factory(stub)).RemarkLegacyAsync(_ => { });

        Assert.Equal(2, count);
        var stack = stub.Requests.Single(r => r.Method == HttpMethod.Put && r.Path.StartsWith("stacks/", StringComparison.Ordinal));
        Assert.Equal("stacks/mattiverse-matdrive", stack.Path);
        Assert.Equal("matos", stack.Body!["managedBy"]!.GetValue<string>());
        Assert.False(stack.Body["deploy"]!.GetValue<bool>());
        Assert.Equal("services: {}", stack.Body["composeYaml"]!.GetValue<string>());
        Assert.Equal("A=1", stack.Body["envContent"]!.GetValue<string>());

        var schedule = stub.Requests.Single(r => r.Method == HttpMethod.Put && r.Path.StartsWith("backup-schedules/", StringComparison.Ordinal));
        Assert.Equal("backup-schedules/Nightly", schedule.Path);
        Assert.Equal("matos", schedule.Body!["managedBy"]!.GetValue<string>());
        Assert.Null(schedule.Body["stack"]);
        Assert.Equal("03:00", schedule.Body["time"]!.GetValue<string>());
        Assert.Equal(2, schedule.Body["targetId"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_app_routes_Mattiverse_made_in_Matcad_get_the_mark_of_MatOS_before_they_are_bound()
    {
        var stub = new Stub().Answer("GET routes/manual", """
            [{"id":4,"name":"MatDrive","host":"matdrive.example.com","wildcard":false,"upstream":"http://mattiverse-matdrive-web:80","enabled":true,"managedBy":"mattiverse","binding":null,"allowedIps":["10.0.0.0/8"]},
             {"id":5,"name":"MatDo","host":"matdo.example.com","upstream":"http://x:80","managedBy":"mattiverse","binding":"matdo"},
             {"id":6,"name":"Mine","host":"mine.example.com","upstream":"http://y:80","managedBy":null},
             {"id":7,"name":"Other","host":"other.example.com","upstream":"http://z:80","managedBy":"mattiverse"}]
            """);
        var hosts = new HashSet<string>(["matdrive.example.com", "matdo.example.com", "mine.example.com"], StringComparer.OrdinalIgnoreCase);

        var remarked = await new MatcadClient(new Locator(), new Factory(stub)).RemarkLegacyRoutesAsync(hosts);

        Assert.Equal(["matdrive.example.com"], remarked);
        var body = stub.Requests.Single(r => r.Method == HttpMethod.Post).Body!;
        Assert.Equal(4, body["id"]!.GetValue<int>());
        Assert.Equal("proxy", body["target"]!.GetValue<string>());
        Assert.Equal("http://mattiverse-matdrive-web:80", body["upstream"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        // What the call does not carry, Matcad keeps (the access fields).
        Assert.False(body.AsObject().ContainsKey("allowedIps"));
    }

    [Fact]
    public void On_the_network_of_Mattiverse_MatOS_also_answers_to_the_old_container_name()
    {
        Assert.Equal(["network", "connect", "--alias", "mattiverse", "mattiverse", "matos"], DockerService.ConnectArguments("mattiverse", "matos"));
        Assert.Equal(["network", "connect", "matos", "matos"], DockerService.ConnectArguments("matos", "matos"));
        Assert.Equal(["network", "connect", "--alias", "mattiverse-dev", "mattiverse", "matos-dev"], DockerService.ConnectArguments("mattiverse", "matos-dev"));
        Assert.Equal(["network", "connect", "mattiverse", "mattiverse"], DockerService.ConnectArguments("mattiverse", "mattiverse"));
        Assert.Equal(["network", "connect", "mattiverse", "mvshots"], DockerService.ConnectArguments("mattiverse", "mvshots"));
    }
}
