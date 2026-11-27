using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;

namespace MatOS.Tests;

/// <summary>The clients against a stub that records every request and answers from a table, so the order and the bodies can be asserted.</summary>
public class EngineClientTests
{
    private sealed record Seen(HttpMethod Method, string Path, JsonNode? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _answers = new();
        public List<Seen> Requests { get; } = new();

        public StubHandler Answer(string method, string path, string json)
        {
            _answers[$"{method} {path}"] = json;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/", string.Empty);
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, path, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var key = $"{request.Method} {path}";
            return _answers.TryGetValue(key, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubLocator : EngineLocator
    {
        public StubLocator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://engine.test", "key"));
    }

    private static MatddnsClient Matddns(StubHandler handler) => new(new StubLocator(), new StubFactory(handler));

    private static MatcadClient Matcad(StubHandler handler) => new(new StubLocator(), new StubFactory(handler));

    private static readonly DnsProvider Cloudflare = DnsProviders.Find("cloudflare")!;

    // ------------------------------------------------------------------ Matddns

    [Fact]
    public async Task Matddns_UpsertGroup_RemovesTheRulesOfLeavingRecords_BeforeItShortensTheList()
    {
        var handler = new StubHandler()
            .Answer("GET", "domains", """[{"id":"g1","name":"MatOS","entries":[{"id":"e-a","hostname":"a.example.com"},{"id":"e-b","hostname":"b.example.com"}]}]""")
            .Answer("GET", "rules", """[{"id":"r-a","domainGroupId":"g1","domainEntryId":"e-a"},{"id":"r-b","domainGroupId":"g1","domainEntryId":"e-b"},{"id":"r-x","domainGroupId":"other","domainEntryId":"e-b"}]""")
            .Answer("POST", "domains", """{"id":"g1","entries":[{"id":"e-a","hostname":"a.example.com"}]}""");

        var (groupId, ids) = await Matddns(handler).UpsertGroupAsync(
            "MatOS",
            Cloudflare,
            new Dictionary<string, string> { ["apiToken"] = "t" },
            [new DnsRecord("a.example.com", "a", "example.com")]);

        Assert.Equal("g1", groupId);
        Assert.Equal("e-a", ids["a.example.com"]);

        var order = handler.Requests.Select(r => $"{r.Method} {r.Path}").ToList();
        // Only the rule of the record that leaves (b, in this group) goes, and it goes before the list is shortened.
        Assert.Equal(["GET domains", "GET rules", "DELETE rules/r-b", "POST domains"], order);

        var body = handler.Requests.Last().Body!;
        Assert.Equal("Cloudflare", body["kind"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        Assert.Equal("t", body["cloudflare"]!["apiToken"]!.GetValue<string>());
        Assert.True(body["cloudflare"]!["allowDynamic"]!.GetValue<bool>());
        var entry = Assert.Single(body["entries"]!.AsArray())!;
        Assert.Equal("a", entry["recordName"]!.GetValue<string>());
        Assert.Equal("example.com", entry["domain"]!.GetValue<string>());
    }

    [Fact]
    public async Task Matddns_UpsertGroup_OfANewGroup_DeletesNothing()
    {
        var handler = new StubHandler()
            .Answer("GET", "domains", "[]")
            .Answer("POST", "domains", """{"id":"g1","entries":[]}""");

        await Matddns(handler).UpsertGroupAsync("MatOS", Cloudflare, new Dictionary<string, string>(), []);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Matddns_DeleteGroup_RemovesItsRulesFirst_ThenTheGroup()
    {
        var handler = new StubHandler()
            .Answer("GET", "domains", """[{"id":"g1","name":"matos","entries":[{"id":"e-a","hostname":"a.example.com"}]}]""")
            .Answer("GET", "rules", """[{"id":"r-a","domainGroupId":"g1","domainEntryId":"e-a"},{"id":"r-x","domainGroupId":"g2","domainEntryId":"e-z"}]""");

        await Matddns(handler).DeleteGroupAsync("MatOS");

        var order = handler.Requests.Select(r => $"{r.Method} {r.Path}").ToList();
        Assert.Equal(["GET domains", "GET rules", "DELETE rules/r-a", "DELETE domains/g1"], order);
    }

    [Fact]
    public async Task Matddns_DeleteGroup_SaysWhetherThereWasAGroup()
    {
        var none = new StubHandler().Answer("GET", "domains", "[]");
        var one = new StubHandler()
            .Answer("GET", "domains", """[{"id":"g1","name":"MatOS","entries":[]}]""")
            .Answer("GET", "rules", "[]");

        Assert.False(await Matddns(none).DeleteGroupAsync("MatOS"));
        Assert.DoesNotContain(none.Requests, r => r.Method == HttpMethod.Delete);
        Assert.True(await Matddns(one).DeleteGroupAsync("MatOS"));
        Assert.Contains(one.Requests, r => r.Method == HttpMethod.Delete && r.Path == "domains/g1");
    }

    // ------------------------------------------------------------------ Matcad

    [Fact]
    public async Task Matcad_UpsertWildcardRoute_UsesTheProvider_AndReusesTheIdOfTheExistingRoute()
    {
        var handler = new StubHandler()
            .Answer("GET", "routes/manual", """[{"id":7,"host":"*.apps.example.com","wildcard":true,"managedBy":"matos"}]""");

        await Matcad(handler).UpsertWildcardRouteAsync("Apps.Example.com.", 3, "https://matos.apps.example.com", "MatOS wildcard");

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("routes", post.Path);
        var body = post.Body!;
        Assert.Equal("*.Apps.Example.com", body["host"]!.GetValue<string>());
        Assert.True(body["wildcard"]!.GetValue<bool>());
        Assert.Equal(3, body["providerId"]!.GetValue<long>());
        Assert.Equal("redirect", body["target"]!.GetValue<string>());
        Assert.Equal("https://matos.apps.example.com", body["fallbackUrl"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Matcad_UpsertRoute_NeverSendsAProvider_BecauseMatcadIgnoresItOnSingleHosts()
    {
        var handler = new StubHandler().Answer("GET", "routes/manual", "[]");

        await Matcad(handler).UpsertRouteAsync("app.example.com", "http://matos-app-web:80", "App");

        var body = handler.Requests.Single(r => r.Method == HttpMethod.Post).Body!;
        Assert.Null(body["providerId"]);
        Assert.False(body["wildcard"]!.GetValue<bool>());
        Assert.Equal("proxy", body["target"]!.GetValue<string>());
        Assert.Null(body["id"]); // new route: Matcad creates it
    }

    [Fact]
    public async Task Matcad_RemoveManaged_KeepsWantedRoutes_AndThoseTheUserMade()
    {
        var handler = new StubHandler().Answer("GET", "routes/manual", """
            [{"id":1,"host":"keep.example.com","managedBy":"matos"},
             {"id":2,"host":"old.example.org","managedBy":"matos"},
             {"id":3,"host":"mine.example.org"},
             {"id":4,"host":"theirs.example.org","managedBy":"someone-else"}]
            """);

        var removed = await Matcad(handler).RemoveManagedExceptAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KEEP.example.com" });

        Assert.Equal(["old.example.org"], removed);
        var deletes = handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Path).ToList();
        Assert.Equal(["routes/2"], deletes);
    }

    // ------------------------------------------------------------------ the shared client

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public async Task EngineClient_TurnsAnUnreachableEngine_IntoTheSameExceptionAsAnErrorAnswer()
    {
        var client = new MatcadClient(new StubLocator(), new StubFactory(new RefusingHandler()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListRoutesAsync());

        Assert.Contains("not reachable", ex.Message);
        Assert.Contains("Matcad", ex.Message);
    }

    [Fact]
    public async Task EngineClient_SaysClearly_WhenAnOlderEngineAnswersWithItsWebPage()
    {
        var handler = new StubHandler()
            .Answer("GET", "status", "<!DOCTYPE html><html></html>")
            .Answer("GET", "routes/manual", "<!DOCTYPE html><html></html>");

        // GetStatus swallows the failure (the engine has no API yet); a real call must say why it failed.
        Assert.Null(await Matcad(handler).GetStatusAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Matcad(handler).ListRoutesAsync());

        Assert.Contains("did not answer", ex.Message);
        Assert.Contains("with JSON", ex.Message);
    }

    // ------------------------------------------------------------------ MatDock: backup targets

    [Fact]
    public async Task A_target_is_created_with_the_mark_of_MatOS_and_a_backup_names_its_target()
    {
        var handler = new StubHandler()
            .Answer("POST", "backup-targets", """{"id":9,"name":"Box","type":"sftp"}""")
            .Answer("POST", "stacks/matos-matsplit/backups", """{"operationId":"op1"}""");
        var client = new MatDockClient(new StubLocator(), new StubFactory(handler));

        var created = await client.CreateTargetAsync(new JsonObject { ["name"] = "Box", ["type"] = "sftp" });
        Assert.Equal(9, created["id"]!.GetValue<long>());
        Assert.Equal("matos", handler.Requests[0].Body!["managedBy"]!.GetValue<string>());

        Assert.Equal("op1", await client.StartBackupAsync("matos-matsplit", stopContainers: true, note: null, targetId: 9));
        Assert.Equal(9, handler.Requests[1].Body!["targetId"]!.GetValue<long>());

        await client.StartBackupAsync("matos-matsplit", stopContainers: true, note: null);
        Assert.Null(handler.Requests[2].Body!["targetId"]);
    }
}
