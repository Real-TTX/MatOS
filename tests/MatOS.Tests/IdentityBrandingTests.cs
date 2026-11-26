using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;

namespace MatOS.Tests;

/// <summary>The look of MatOS's sign-in at MatIdentity (Core/Integrations/SelfBrand.cs): the gate for an older MatIdentity and the payload of the brand.</summary>
public class IdentityBrandingTests
{
    private static readonly byte[] Logo = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    private static readonly byte[] Background = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static SelfBrand Brand(string name = "MatOS", byte[]? background = null)
        => new(name, "#8558ec", Logo, "image/svg+xml", background ?? Background, "image/jpeg");

    /// <summary>The brand as MatIdentity answers it when it has exactly <see cref="Brand"/>.</summary>
    private static JsonObject Stored(string name = "MatOS") => new()
    {
        ["name"] = name,
        ["accent"] = "#8558EC",
        ["hideIdentity"] = true,
        ["hasLogo"] = true,
        ["hasBackground"] = true,
        ["logoContentType"] = "image/svg+xml",
        ["backgroundContentType"] = "image/jpeg",
        ["logoUrl"] = "https://id.example.com/branding/matos/logo?v=" + IdentityBranding.Hash(Logo),
        ["backgroundUrl"] = "https://id.example.com/branding/matos/background?v=" + IdentityBranding.Hash(Background),
    };

    [Fact]
    public void Supported_OnlyWhenTheAnswerHasTheFieldBranding()
    {
        Assert.False(IdentityBranding.Supported(JsonNode.Parse("""{"clientId":"matos","displayName":"MatOS"}""")));
        Assert.True(IdentityBranding.Supported(JsonNode.Parse("""{"clientId":"matos","branding":null}""")));
        Assert.True(IdentityBranding.Supported(JsonNode.Parse("""{"clientId":"matos","branding":{"name":"MatOS"}}""")));
        Assert.False(IdentityBranding.Supported(null));
    }

    [Fact]
    public void Hash_IsTheVersionMatIdentityGivesAPicture()
    {
        // MatIdentity's BrandingRules.Hash: the first 16 hex digits (lower case) of the SHA-256.
        Assert.Equal("ba7816bf8f01cfea", IdentityBranding.Hash(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void Payload_WithoutABrand_SendsEverything()
    {
        var payload = IdentityBranding.Payload(Brand(), current: null)!;

        Assert.Equal("MatOS", (string?)payload["name"]);
        Assert.Equal("#8558ec", (string?)payload["accent"]);
        Assert.True((bool)payload["hideIdentity"]!);
        Assert.Equal("image/svg+xml", (string?)payload["logo"]!["contentType"]);
        Assert.Equal(Logo, Convert.FromBase64String((string)payload["logo"]!["data"]!));
        Assert.Equal("image/jpeg", (string?)payload["background"]!["contentType"]);
        Assert.Equal(Background, Convert.FromBase64String((string)payload["background"]!["data"]!));
    }

    [Fact]
    public void Payload_TheSameBrand_SendsNothing()
        => Assert.Null(IdentityBranding.Payload(Brand(), Stored()));

    [Fact]
    public void Payload_ANewName_LeavesThePicturesOut()
    {
        var payload = IdentityBranding.Payload(Brand("Home"), Stored())!;

        Assert.Equal("Home", (string?)payload["name"]);
        // Missing keeps the stored picture at MatIdentity: two megabytes are not sent again for a new name.
        Assert.False(payload.ContainsKey("logo"));
        Assert.False(payload.ContainsKey("background"));
    }

    [Fact]
    public void Payload_AnotherPicture_SendsOnlyThatOne()
    {
        var stored = Stored();
        stored["logoUrl"] = "https://id.example.com/branding/matos/logo?v=0000000000000000";

        var payload = IdentityBranding.Payload(Brand(), stored)!;

        Assert.True(payload.ContainsKey("logo"));
        Assert.False(payload.ContainsKey("background"));
    }

    [Fact]
    public void Payload_IdentityShown_IsHiddenAgain()
    {
        var stored = Stored();
        stored["hideIdentity"] = false;

        Assert.True((bool)IdentityBranding.Payload(Brand(), stored)!["hideIdentity"]!);
    }

    [Fact]
    public void NameOf_IsTheInstanceName_TrimmedAndShortEnough()
    {
        Assert.Equal("MatOS", IdentityBranding.NameOf(null));
        Assert.Equal("MatOS", IdentityBranding.NameOf("  "));
        Assert.Equal("Home", IdentityBranding.NameOf(" Home "));
        Assert.Equal(IdentityBranding.MaxName, IdentityBranding.NameOf(new string('a', 80)).Length);
    }

    [Fact]
    public void Assets_WithoutALogo_GiveNoBrand()
        => Assert.Null(new SelfBrandAssets("#8558ec", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".svg"), null).For("MatOS"));

    [Fact]
    public void Assets_OfTheWebProject_AreThePlanetAndTheOrbitWallpaper()
    {
        var root = Path.Combine(RepoRoot(), "src", "MatOS.Web", "wwwroot", "img");
        var brand = new SelfBrandAssets("#8558ec", Path.Combine(root, "logo.svg"), Path.Combine(root, "wallpapers", "orbit-light.jpg")).For("MatOS")!;

        // MatIdentity's limits: the logo at most 256 KiB, the background 2 MiB.
        Assert.InRange(brand.Logo.Length, 1, 256 * 1024);
        Assert.Contains("<svg", Encoding.UTF8.GetString(brand.Logo), StringComparison.Ordinal);
        Assert.InRange(brand.Background!.Length, 1, 2 * 1024 * 1024);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, brand.Background[..3]);
    }

    // ------------------------------------------------------------------ MatIdentityClient.BrandAsync

    [Fact]
    public async Task BrandAsync_AnOlderMatIdentity_GetsNothing()
    {
        var handler = new StubHandler("""{"clientId":"matos","displayName":"MatOS"}""");

        Assert.Equal(BrandingOutcome.Unsupported, await Client(handler).BrandAsync("matos", Brand(), CancellationToken.None));
        Assert.Equal(new[] { "GET clients/matos" }, handler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task BrandAsync_NoBrandYet_PutsTheBrandWithTheMark()
    {
        var handler = new StubHandler("""{"clientId":"matos","branding":null}""");

        Assert.Equal(BrandingOutcome.Sent, await Client(handler).BrandAsync("matos", Brand(), CancellationToken.None));
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal("clients/matos", put.Path);
        Assert.Equal("matos", (string?)put.Body!["managedBy"]);
        // Only managedBy and the brand: the rest of the client stays as the registration left it.
        Assert.Equal(new[] { "managedBy", "branding" }, put.Body.AsObject().Select(p => p.Key));
        Assert.True((bool)put.Body["branding"]!["hideIdentity"]!);
    }

    [Fact]
    public async Task BrandAsync_TheSameBrand_PutsNothing()
    {
        var handler = new StubHandler(new JsonObject { ["clientId"] = "matos", ["branding"] = Stored() }.ToJsonString());

        Assert.Equal(BrandingOutcome.Unchanged, await Client(handler).BrandAsync("matos", Brand(), CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    private static MatIdentityClient Client(StubHandler handler) => new(new StubLocator(), new StubFactory(handler));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mattiverse.slnx")) && !Directory.Exists(Path.Combine(dir.FullName, "src", "MatOS.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository was not found.");
    }

    private sealed record Seen(HttpMethod Method, string Path, JsonNode? Body);

    /// <summary>Answers every GET with the client <c>get</c>, every PUT with an empty object, and records the requests.</summary>
    private sealed class StubHandler(string get) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath.Replace("/api/v1/", string.Empty), string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var json = request.Method == HttpMethod.Get ? get : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
}
