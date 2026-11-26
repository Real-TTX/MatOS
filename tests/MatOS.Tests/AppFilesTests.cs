using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

public class AppFilesTests
{
    private static readonly IOptions<MatOSOptions> Options = Microsoft.Extensions.Options.Options.Create(new MatOSOptions());

    private static ComposeRenderer Renderer() => new(Options);

    private static ComposeFiles CreateFiles(string? data = null)
        => new(new AppPaths(data ?? Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), Renderer());

    private static string ReadCatalog(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static CatalogApp Entry(string id) => CatalogParser.Parse(ReadCatalog(id), id + ".yml");

    private static InstalledApp AppOf(string slug, string? uiService, int? uiPort, int? hostPort)
        => new() { Slug = slug, Name = slug, ProjectName = "matos-" + slug, UiService = uiService, UiPort = uiPort, HostPort = hostPort };

    /// <summary>The rendered compose as plain dictionaries, to assert on what compose will read.</summary>
    private static Dictionary<string, object> Parse(string yaml)
        => new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

    private static IDictionary<object, object> Service(Dictionary<string, object> compose, string name)
        => (IDictionary<object, object>)((IDictionary<object, object>)compose["services"])[name];

    [Fact]
    public void An_app_the_proxy_routes_publishes_no_port_and_the_proxy_publishes_its_port_routes()
    {
        var own = Parse(Renderer().Render(Entry("matdo"), AppOf("matdo", "web", 6006, 6006)));
        Assert.Contains("6006:6006", ((IEnumerable<object>)Service(own, "web")["ports"]).Select(p => p.ToString()));

        var routed = Parse(Renderer().Render(Entry("matdo"), AppOf("matdo", "web", 6006, 6006), ports: new ComposePorts(false, [])));
        Assert.False(Service(routed, "web").ContainsKey("ports"));

        // Matcad: its UI keeps its own port; the route service publishes 80 and 443 and the port of every app it serves without a domain.
        var proxy = Parse(Renderer().Render(Entry("matcad"), AppOf("matcad", "matcad", 4433, 4433), ports: new ComposePorts(true, [6006, 8080])));
        Assert.Contains("4433:4433", ((IEnumerable<object>)Service(proxy, "matcad")["ports"]).Select(p => p.ToString()));
        Assert.Equal(["80:80", "443:443", "443:443/udp", "6006:6006", "8080:8080"], ((IEnumerable<object>)Service(proxy, "caddy")["ports"]).Select(p => p.ToString()));
    }

    [Fact]
    public void The_ports_an_entry_declares_are_read_with_the_defaults_of_their_variables()
    {
        Assert.Equal(new HashSet<int> { 80, 443 }, ComposeRenderer.DeclaredHostPorts(Entry("matcad"), "caddy"));
        Assert.Contains(2587, ComposeRenderer.DeclaredHostPorts(Entry("matsmtp"), "matsmtp"));
        Assert.Empty(ComposeRenderer.DeclaredHostPorts(Entry("matcad"), "matcad"));
        Assert.Empty(ComposeRenderer.DeclaredHostPorts(Entry("matcad"), "nothing"));
        Assert.Equal("caddy", Entry("matcad").Routes?.Service);
        Assert.Null(Entry("matdo").Routes);
    }

    [Fact]
    public void Render_NamesEveryContainer_AndLabelsIt()
    {
        var compose = Parse(Renderer().Render(Entry("matcad"), AppOf("matcad", "matcad", 4433, 4433)));

        var caddy = Service(compose, "caddy");
        Assert.Equal("matos-matcad-caddy", caddy["container_name"]);
        Assert.Equal("matos-matcad-matcad", Service(compose, "matcad")["container_name"]);

        var labels = (IDictionary<object, object>)caddy["labels"];
        Assert.Equal("matcad", labels["matos.instance"]);
        Assert.Equal("caddy", labels["matos.service"]);
        Assert.Equal("true", labels["matos.managed"]);
    }

    [Fact]
    public void Render_AppendsTheUiPort_ToTheServiceThatHasThePorts()
    {
        var compose = Parse(Renderer().Render(Entry("matmail"), AppOf("matmail", "matmail", 9933, 9100)));

        var ports = ((IList<object>)Service(compose, "matmail")["ports"]).Select(p => p.ToString()).ToList();

        // The catalog's own mail ports stay, the UI port is added after them.
        Assert.Contains("${SMTP_PORT:-25}:25", ports);
        Assert.Equal("9100:9933", ports[^1]);
        Assert.False(Service(compose, "db").ContainsKey("ports"));
    }

    [Fact]
    public void Render_PublishesTheUiPort_OfAServiceWithoutPorts()
    {
        var compose = Parse(Renderer().Render(Entry("matcad"), AppOf("matcad", "matcad", 4433, 4500)));

        Assert.Equal(["4500:4433"], ((IList<object>)Service(compose, "matcad")["ports"]).Select(p => p.ToString()!).ToArray());
        // Caddy keeps only the ports the catalog gave it.
        Assert.Equal(3, ((IList<object>)Service(compose, "caddy")["ports"]).Count);
    }

    [Fact]
    public void Render_JoinsTheSharedNetwork_OnlyWhereTheProxyMustReach()
    {
        var matcad = Parse(Renderer().Render(Entry("matcad"), AppOf("matcad", "matcad", 4433, 4433)));
        var matmail = Parse(Renderer().Render(Entry("matmail"), AppOf("matmail", "matmail", 9933, 9933)));

        // Matcad's UI service and its Caddy (listed as shared) join the network; the UI service of MatMail does, its database does not.
        Assert.Contains("matos", ((IList<object>)Service(matcad, "caddy")["networks"]).Select(n => n.ToString()));
        Assert.Contains("matos", ((IList<object>)Service(matcad, "matcad")["networks"]).Select(n => n.ToString()));
        Assert.Contains("matos", ((IList<object>)Service(matmail, "matmail")["networks"]).Select(n => n.ToString()));
        Assert.False(Service(matmail, "db").ContainsKey("networks"));

        var network = (IDictionary<object, object>)((IDictionary<object, object>)matmail["networks"])["matos"];
        Assert.Equal("matos", network["name"]);
        Assert.Equal("true", network["external"].ToString()!.ToLowerInvariant());
    }

    [Fact]
    public void Render_KeepsTheVariablePlaceholders_ForComposeToResolve()
    {
        var yaml = Renderer().Render(Entry("matcad"), AppOf("matcad", "matcad", 4433, 4433));

        Assert.Contains("${MATCAD_API_KEY}", yaml);
        Assert.DoesNotContain("\r", yaml);
        Assert.False(yaml.TrimEnd().EndsWith("...", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_AddsTheBlockMatDockShowsAsAnAppTile_AndKeepsOurOwn()
    {
        var app = AppOf("matddns", "matddns", 8080, 4060);
        app.Name = "Matddns (home)";

        var compose = Parse(Renderer().Render(Entry("matddns"), app));

        var block = (IDictionary<object, object>)compose["x-matdock"];
        Assert.Equal("Matddns (home)", block["name"]);
        Assert.Equal("Infrastructure", block["category"]);
        Assert.True(compose.ContainsKey("x-matos"));
    }

    [Fact]
    public void Render_MergesWithLabelsAndNetworksThatTheEntryAlreadyHas()
    {
        var entry = CatalogParser.Parse("""
            x-matos:
              id: custom
              name: Custom
              ui: { service: web, port: 80 }
            services:
              web:
                image: nginx
                labels:
                  - "traefik.enable=false"
                networks:
                  back: {}
            networks:
              back: {}
            """, "custom.yml");

        var compose = Parse(Renderer().Render(entry, AppOf("custom", "web", 80, 8100)));

        var web = Service(compose, "web");
        var labels = ((IList<object>)web["labels"]).Select(l => l.ToString()).ToList();
        Assert.Contains("traefik.enable=false", labels);
        Assert.Contains("matos.instance=custom", labels);

        var networks = (IDictionary<object, object>)web["networks"];
        Assert.True(networks.ContainsKey("back") && networks.ContainsKey("default") && networks.ContainsKey("matos"));
        var top = (IDictionary<object, object>)compose["networks"];
        Assert.True(top.ContainsKey("back") && top.ContainsKey("matos"));
    }

    [Fact]
    public void Render_EveryBuiltInEntry_ProducesAComposeFileThatStillParsesAsAnEntry()
    {
        var catalog = new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogService>.Instance);

        foreach (var entry in catalog.GetAll())
        {
            var app = AppOf(entry.Id, entry.Ui?.Service, entry.Ui?.Port, entry.Ui?.HostPort);
            var rendered = Renderer().Render(entry, app);

            var again = CatalogParser.Parse(rendered, entry.Id);
            Assert.Equal(entry.ServiceNames, again.ServiceNames);
            Assert.Equal(entry.Variables.Count, again.Variables.Count);

            var compose = Parse(rendered);
            foreach (var service in entry.ServiceNames)
            {
                Assert.Equal($"matos-{entry.Id}-{service}", Service(compose, service)["container_name"]);
            }
        }
    }

    [Fact]
    public void ProjectAndContainerNames_UseThePrefix()
    {
        var files = CreateFiles();

        Assert.Equal("matos-matcad", files.ProjectName("matcad"));
        Assert.Equal("matos-matcad-caddy", files.ContainerName("matcad", "caddy"));
    }

    [Fact]
    public async Task Build_AndWrite_ProduceTheFilesAnEngineDeploys()
    {
        var data = Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"));
        var files = CreateFiles(data);
        var app = AppOf("matcad", "matcad", 4433, 4433);

        var spec = files.Build(app, Entry("matcad"), new Dictionary<string, string> { ["MATCAD_API_KEY"] = "k3y" }, "nas.local");
        await files.WriteAsync(spec);
        var read = await files.ReadAsync("matcad", "matos-matcad");

        Assert.Equal("matos-matcad", spec.Name);
        Assert.Contains("MATCAD_API_KEY='k3y'", spec.Env);
        Assert.Contains("MV_PUBLIC_HOST='nas.local'", spec.Env);
        Assert.Contains("MV_SLUG='matcad'", spec.Env);
        Assert.NotNull(read);
        Assert.Equal(spec.Compose, read!.Compose);
        Assert.True(files.Exists("matcad"));
        Assert.False(File.Exists(Path.Combine(files.Folder("matcad"), "docker-compose.override.yml")));
    }

    [Fact]
    public void EnvFile_RoundTrips_AndQuotesSpecialCharacters()
    {
        var env = AppVariables.BuildEnvFile(
            new Dictionary<string, string> { ["MV_SLUG"] = "x" },
            new Dictionary<string, string> { ["PASSWORD"] = "pa$$ w#rd" });

        var parsed = EnvFileParser.Parse(env);

        Assert.Contains("PASSWORD='pa$$ w#rd'", env);
        Assert.Equal("pa$$ w#rd", parsed["PASSWORD"]);
        Assert.True(EnvFileParser.IsBuiltIn("MV_SLUG"));
        Assert.False(EnvFileParser.IsBuiltIn("PASSWORD"));
    }

    [Fact]
    public void ComposeHash_IgnoresFormatting_ButNotMeaning()
    {
        var a = "services:\n  web:\n    image: nginx\n    ports:\n      - \"80:80\"\n";
        var reformatted = "# comment\r\nservices:\r\n  web:\r\n    ports: ['80:80']\r\n    image: \"nginx\"\r\n\r\n";
        var changed = "services:\n  web:\n    image: nginx:1.27\n    ports:\n      - \"80:80\"\n";

        Assert.Equal(ComposeHash.Of(a), ComposeHash.Of(reformatted));
        Assert.NotEqual(ComposeHash.Of(a), ComposeHash.Of(changed));
        Assert.Equal(ComposeHash.Of("not: [valid"), ComposeHash.Of("not: [valid\r\n"));
    }

}
