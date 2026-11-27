using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// Every app in the window: the frame headers MatOS puts on Matcad's route of an app that does not take MAT_FRAME_ANCESTORS (FrameHeaders), the body that
/// writes them (MatcadClient) and what the window then sees through the route (FrameHeaders.Through + FramePolicy).
/// </summary>
public class RouteFrameHeaderTests
{
    private static readonly string[] Origins = ["http://localhost:8030", "http://127.0.0.1:8030"];

    private static readonly FrameAnswer Deny = new(["DENY"], null);

    // ------------------------------------------------------------------ the decision

    [Fact]
    public void An_app_without_a_policy_of_its_own_gets_frame_ancestors_naming_MatOS_and_no_X_Frame_Options()
    {
        var wanted = FrameHeaders.Wanted(takesOrigins: false, Deny, Origins);

        Assert.Equal(
            [new RouteHeader("X-Frame-Options", ""), new RouteHeader("Content-Security-Policy", "frame-ancestors 'self' http://localhost:8030 http://127.0.0.1:8030")],
            wanted);
        // Also an app that sends neither: from then on only MatOS (and the app itself) may frame it.
        Assert.Equal(2, FrameHeaders.Wanted(false, new FrameAnswer(null, null), Origins)!.Count);
    }

    [Fact]
    public void An_app_with_a_policy_of_its_own_keeps_it_whole_and_MatOS_adds_nothing()
    {
        // Matcad can only replace the whole header: its other directives would be lost, and removing only X-Frame-Options would let every site frame it.
        var own = new FrameAnswer(["SAMEORIGIN"], ["default-src 'self'; script-src 'self'"]);
        Assert.Empty(FrameHeaders.Wanted(false, own, Origins)!);
    }

    [Fact]
    public void An_app_that_takes_MAT_FRAME_ANCESTORS_names_MatOS_itself()
        => Assert.Empty(FrameHeaders.Wanted(takesOrigins: true, null, Origins)!);

    [Fact]
    public void While_the_app_does_not_answer_the_route_keeps_what_it_has()
        => Assert.Null(FrameHeaders.Wanted(false, null, Origins));

    [Fact]
    public void Too_many_origins_for_one_header_are_not_cut_but_left_out()
    {
        var many = Enumerable.Range(0, 60).Select(i => $"http://host-{i}.example.com:8030").ToList();
        Assert.Empty(FrameHeaders.Wanted(false, Deny, many)!);
        Assert.Empty(FrameHeaders.Wanted(false, Deny, [])!);
    }

    [Fact]
    public void The_catalog_says_which_apps_take_MAT_FRAME_ANCESTORS()
    {
        var catalog = new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);
        foreach (var engine in EngineIds.All)
        {
            Assert.True(FrameHeaders.TakesOrigins(catalog.Get(engine)), engine);
        }

        Assert.False(FrameHeaders.TakesOrigins(catalog.Get("matmail")));
        Assert.False(FrameHeaders.TakesOrigins(catalog.Get("portainer")));
        Assert.False(FrameHeaders.TakesOrigins(null));
    }

    // ------------------------------------------------------------------ the route's list

    [Fact]
    public void The_headers_of_MatOS_join_those_an_administrator_set_and_replace_only_their_own()
    {
        var current = new List<RouteHeader> { new("Referrer-Policy", "no-referrer"), new("Content-Security-Policy", "frame-ancestors 'self' http://old:8030") };
        var wanted = FrameHeaders.Wanted(false, Deny, Origins)!;

        var merged = FrameHeaders.Merge(current, wanted)!;

        Assert.Contains(new RouteHeader("Referrer-Policy", "no-referrer"), merged);
        Assert.Contains(new RouteHeader("X-Frame-Options", ""), merged);
        Assert.Single(merged, h => h.Name == "Content-Security-Policy");
        Assert.Contains(merged, h => h.Value == "frame-ancestors 'self' http://localhost:8030 http://127.0.0.1:8030");
    }

    [Fact]
    public void Nothing_is_written_when_the_route_has_them_already()
    {
        var wanted = FrameHeaders.Wanted(false, Deny, Origins)!;
        Assert.Null(FrameHeaders.Merge([.. wanted.Reverse()], wanted));
        Assert.Null(FrameHeaders.Merge([], []));
    }

    [Fact]
    public void They_come_off_when_the_app_does_not_need_them_any_more()
    {
        var current = new List<RouteHeader>([.. FrameHeaders.Wanted(false, Deny, Origins)!, new("Cache-Control", "no-store")]);
        Assert.Equal([new RouteHeader("Cache-Control", "no-store")], FrameHeaders.Merge(current, []));
    }

    [Theory]
    [InlineData("X-Frame-Options", "SAMEORIGIN")]
    [InlineData("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none'")]
    public void A_frame_header_an_administrator_set_in_Matcad_wins(string name, string value)
        => Assert.Null(FrameHeaders.Merge([new RouteHeader(name, value)], FrameHeaders.Wanted(false, Deny, Origins)!));

    // ------------------------------------------------------------------ what the window sees through the route

    [Fact]
    public void Through_the_route_the_window_of_MatOS_may_show_the_app_and_another_site_may_not()
    {
        var seen = FrameHeaders.Through(Deny, FrameHeaders.Wanted(false, Deny, Origins)!, allowEmbedding: false);

        Assert.Null(seen.XFrameOptions);
        Assert.True(FramePolicy.Decide(seen.XFrameOptions, seen.ContentSecurityPolicies, "http://127.0.0.1:8030", "http://127.0.0.1:8104").Allowed);
        Assert.False(FramePolicy.Decide(seen.XFrameOptions, seen.ContentSecurityPolicies, "https://evil.example.com", "http://127.0.0.1:8104").Allowed);
        // Without the route the app refuses.
        Assert.False(FramePolicy.Decide(Deny.XFrameOptions, Deny.ContentSecurityPolicies, "http://127.0.0.1:8030", "http://127.0.0.1:8104").Allowed);
    }

    [Fact]
    public void Allow_embedding_on_the_route_cuts_frame_ancestors_out_and_keeps_the_rest()
    {
        var own = new FrameAnswer(["DENY"], ["default-src 'self'; frame-ancestors 'none'; img-src data:"]);
        var seen = FrameHeaders.Through(own, [], allowEmbedding: true);

        Assert.Null(seen.XFrameOptions);
        Assert.Equal(["default-src 'self'; img-src data:"], seen.ContentSecurityPolicies);
    }

    // ------------------------------------------------------------------ Matcad

    [Fact]
    public void The_route_is_written_back_unchanged_with_the_mark_of_MatOS_and_the_headers()
    {
        // A port route (no domain): Matcad shows its host empty.
        var route = JsonNode.Parse("""
            {"id":7,"name":"Matmon","host":"","wildcard":false,"upstream":"http://matos-matmon-web:8080","insecureSkipVerify":false,"fallbackUrl":null,
             "redirectPermanent":false,"authenticationId":null,"providerId":null,"acmeEmail":null,"enabled":true,"allowEmbedding":false,"listenPort":8104,
             "managedBy":"matos","binding":"matmon","target":"proxy","responseHeaders":[{"name":"Cache-Control","value":"no-store"}]}
            """)!.AsObject();
        var headers = FrameHeaders.Merge(MatcadClient.ResponseHeadersOf(route), FrameHeaders.Wanted(false, Deny, Origins)!)!;

        var body = MatcadClient.ResponseHeadersBody(route, headers);

        Assert.Equal(7, body["id"]!.GetValue<long>());
        Assert.Equal(8104, body["listenPort"]!.GetValue<int>());
        Assert.Equal("http://matos-matmon-web:8080", body["upstream"]!.GetValue<string>());
        Assert.Equal("proxy", body["target"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        // The binding is Matcad's to keep: the call does not carry it.
        Assert.Null(body["binding"]);
        Assert.Equal(
            """[{"name":"Cache-Control","value":"no-store"},{"name":"X-Frame-Options","value":""},{"name":"Content-Security-Policy","value":"frame-ancestors 'self' http://localhost:8030 http://127.0.0.1:8030"}]""",
            body["responseHeaders"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public async Task Only_a_Matcad_that_names_the_capability_gets_response_headers()
    {
        var current = new Stub("""{"ok":true,"version":"0.13.0","apiVersion":3,"capabilities":["routeGuards","responseHeaders","passthrough"]}""");
        var older = new Stub("""{"ok":true,"version":"0.12.0","apiVersion":2,"capabilities":["routeGuards"]}""");
        var oldest = new Stub("""{"ok":true,"version":"0.9.0","apiVersion":1}""");

        Assert.True(await Matcad(current).HasCapabilityAsync(MatcadClient.ResponseHeadersCapability));
        Assert.False(await Matcad(older).HasCapabilityAsync(MatcadClient.ResponseHeadersCapability));
        Assert.False(await Matcad(oldest).HasCapabilityAsync(MatcadClient.ResponseHeadersCapability));
    }

    [Fact]
    public async Task The_route_of_an_app_is_its_binding()
    {
        var stub = new Stub("{}", """[{"id":1,"host":"matos.example.com","binding":null},{"id":2,"host":"matmon.example.com","binding":"matmon"}]""");
        Assert.Equal(2, (await Matcad(stub).RouteOfBindingAsync("matmon"))!["id"]!.GetValue<long>());
        Assert.Null(await Matcad(stub).RouteOfBindingAsync("matsplit"));
    }

    private static MatcadClient Matcad(Stub stub) => new(new StubLocator(), stub);

    private sealed class Stub(string status, string routes = "[]") : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/status") ? status : routes, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StubLocator : EngineLocator
    {
        public StubLocator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://engine.test", "key"));
    }
}
