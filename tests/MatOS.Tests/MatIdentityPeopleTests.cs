using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// The People area talks to MatIdentity through SCIM (MatDesign docs/PLATFORM.md "SCIM at MatIdentity"): what it reads of a person, a group and an app, what it
/// writes (PATCH bodies with the grants, the actor), and how a refusal of MatIdentity reaches the page.
/// </summary>
public sealed class MatIdentityPeopleTests
{
    private const string Anna = """
        { "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:mat:scim:schemas:extension:access:1.0:User"],
          "id": "sub-anna", "userName": "anna", "displayName": "Anna Berg", "active": true,
          "emails": [ { "value": "anna@example.test", "primary": true } ],
          "groups": [ { "value": "3", "display": "Family" } ],
          "urn:mat:scim:schemas:extension:access:1.0:User": {
            "administrator": false,
            "grants": [ { "app": "matdrive", "role": "user", "permissions": ["share-public"], "quotas": { "storage": 53687091200, "ai-tokens": null } } ],
            "access": [ { "app": "matdrive", "role": "user", "permissions": ["share-public"], "quotas": { "storage": 53687091200 }, "via": ["person", "group:Family"] } ] } }
        """;

    [Fact]
    public async Task A_person_comes_with_groups_grants_and_access()
    {
        var handler = new ScimHandler { Answer = _ => (HttpStatusCode.OK, Anna) };
        var anna = await People(handler).PersonAsync("sub-anna");

        Assert.NotNull(anna);
        Assert.Equal("Anna Berg", anna.DisplayName);
        Assert.Equal("anna@example.test", anna.Email);
        Assert.Equal(("3", "Family"), Assert.Single(anna.Groups));
        var grant = Assert.Single(anna.Grants);
        Assert.Equal(53687091200, grant.Quotas["storage"]);
        Assert.True(grant.Quotas.ContainsKey("ai-tokens"));
        Assert.Null(grant.Quotas["ai-tokens"]);
        Assert.Equal(["person", "group:Family"], Assert.Single(anna.Access).Via);
        Assert.Equal("http://matidentity.test/scim/v2/Users/sub-anna", handler.Requests.Single().Url);
        Assert.Equal("the-key", handler.Requests.Single().Key);
    }

    [Fact]
    public async Task Every_page_of_a_list_is_read()
    {
        var handler = new ScimHandler
        {
            Answer = r => (HttpStatusCode.OK, r.Url.Contains("startIndex=1", StringComparison.Ordinal)
                ? $$"""{ "totalResults": 2, "Resources": [ {{Anna}} ] }"""
                : """{ "totalResults": 2, "Resources": [ { "id": "sub-ben", "userName": "ben" } ] }"""),
        };

        var people = await People(handler).PeopleAsync();

        Assert.Equal(["sub-anna", "sub-ben"], people.Select(p => p.Subject));
        Assert.Contains(handler.Requests, r => r.Url.Contains("startIndex=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_grants_go_as_a_patch_of_the_extension_with_the_person_who_acted()
    {
        var handler = new ScimHandler { Answer = _ => (HttpStatusCode.OK, Anna) };
        await People(handler).SetPersonGrantsAsync("sub-anna",
            [new IdentityGrant("matdrive", "user", ["share-public"], new Dictionary<string, long?> { ["storage"] = 1024, ["ai-tokens"] = null })], "sub-admin");

        var request = handler.Requests.Single();
        Assert.Equal("PATCH", request.Method);
        Assert.Equal("sub-admin", request.Actor);
        var operation = request.Body!["Operations"]![0]!;
        Assert.Equal("replace", operation["op"]!.GetValue<string>());
        Assert.Equal("urn:mat:scim:schemas:extension:access:1.0:User:grants", operation["path"]!.GetValue<string>());
        var grant = operation["value"]![0]!;
        Assert.Equal("matdrive", grant["app"]!.GetValue<string>());
        Assert.Equal(1024, grant["quotas"]!["storage"]!.GetValue<long>());
        Assert.True(((JsonObject)grant["quotas"]!).ContainsKey("ai-tokens"));
        Assert.Null(grant["quotas"]!["ai-tokens"]);
    }

    [Fact]
    public async Task Joining_and_leaving_groups_patches_only_the_groups_that_change()
    {
        var handler = new ScimHandler
        {
            Answer = r => (HttpStatusCode.OK, r.Method == "GET"
                ? """{ "totalResults": 3, "Resources": [ { "id": "1", "displayName": "In", "members": [ { "value": "sub-anna" } ] }, { "id": "2", "displayName": "Out", "members": [ { "value": "sub-anna" } ] }, { "id": "3", "displayName": "New", "members": [] } ] }"""
                : """{ "id": "x", "displayName": "x" }"""),
        };

        await People(handler).SetGroupsOfAsync("sub-anna", new HashSet<string> { "1", "3" }, null);

        var patches = handler.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.Equal(2, patches.Count);
        var leave = patches.Single(p => p.Url.EndsWith("/Groups/2", StringComparison.Ordinal)).Body!["Operations"]![0]!;
        Assert.Equal("remove", leave["op"]!.GetValue<string>());
        Assert.Equal("members[value eq \"sub-anna\"]", leave["path"]!.GetValue<string>());
        var join = patches.Single(p => p.Url.EndsWith("/Groups/3", StringComparison.Ordinal)).Body!["Operations"]![0]!;
        Assert.Equal("add", join["op"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_apps_come_with_their_roles_permissions_and_quotas()
    {
        var handler = new ScimHandler
        {
            Answer = _ => (HttpStatusCode.OK, """
                [ { "clientId": "matdrive", "displayName": "MatDrive", "allowSignIn": true, "roleMode": "matidentity", "openToEveryone": false,
                    "roles": [ { "key": "admin", "label": "Administrator" }, { "key": "user", "label": "User" } ],
                    "permissions": [ { "key": "share-public", "label": "Share publicly", "roles": ["admin"] } ],
                    "quotas": [ { "key": "storage", "label": "Storage", "unit": "bytes", "min": 0 } ] },
                  { "clientId": "matos", "displayName": "MatOS", "allowSignIn": true, "roleMode": "app", "roles": [], "permissions": [], "quotas": [] } ]
                """),
        };

        var apps = await People(handler).AppsAsync();

        var drive = apps.Single(a => a.ClientId == "matdrive");
        Assert.True(drive.RolesByIdentity);
        Assert.Equal(["admin"], drive.Permissions.Single().Roles);
        Assert.Equal("GB", MatIdentityPeople.UnitOf(drive.Quotas.Single()));
        Assert.Equal("50", MatIdentityPeople.Show(drive.Quotas.Single(), 50L * 1024 * 1024 * 1024));
        Assert.False(apps.Single(a => a.ClientId == "matos").RolesByIdentity);
        Assert.Equal("http://matidentity.test/api/v1/clients", handler.Requests.Single().Url);
    }

    [Theory]
    // An older MatIdentity sends /scim/v2 to its sign-in page: a redirect (never followed), or the page itself.
    [InlineData(HttpStatusCode.Redirect, "text/html", "")]
    [InlineData(HttpStatusCode.OK, "text/html", "<!DOCTYPE html><html><body>Sign in</body></html>")]
    [InlineData(HttpStatusCode.NotFound, "text/html", "<html>Not found</html>")]
    [InlineData(HttpStatusCode.OK, "application/json", "{ not json")]
    public async Task A_matidentity_without_scim_says_so_instead_of_an_error_of_the_parser(HttpStatusCode status, string type, string body)
    {
        var handler = new ScimHandler { Answer = _ => (status, body), ContentType = type };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => People(handler).PersonAsync("sub-anna"));
        Assert.Equal(MatIdentityPeople.NoScim, error.Message);
    }

    [Fact]
    public async Task A_refusal_of_matidentity_reaches_the_page_in_its_words()
    {
        var handler = new ScimHandler { Answer = _ => (HttpStatusCode.Conflict, """{ "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"], "status": "409", "detail": "The user name \"anna\" is taken." }""") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => People(handler).CreatePersonAsync(new IdentityPersonInput("anna", "Anna", null, true, false, "Kiwi-Garden-77"), null));
        Assert.Equal("The user name \"anna\" is taken.", error.Message);

        // An action that cannot run now answers 409 with a message: that is an answer, not an error.
        handler.Answer = _ => (HttpStatusCode.Conflict, """{ "ok": false, "message": "The person has no e-mail address." }""");
        var (ok, message) = await People(handler).RunActionAsync("sub-anna", "invite", null);
        Assert.False(ok);
        Assert.Equal("The person has no e-mail address.", message);

        handler.Answer = _ => (HttpStatusCode.NotFound, """{ "detail": "There is no User." }""");
        Assert.Null(await People(handler).PersonAsync("sub-nobody"));
    }

    [Fact]
    public async Task The_writes_of_a_MatOS_signed_in_through_MatIdentity_name_its_brand_for_the_mails()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N")));
        var config = new SystemConfigService(new JsonConfigStore(paths, NullLogger<JsonConfigStore>.Instance), new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
        var handler = new ScimHandler { Answer = _ => (HttpStatusCode.OK, """{ "ok": true, "message": "sent" }""") };
        var people = new MatIdentityPeople(new StubLocator(), new StubFactory(handler), config);

        // Not signed in through MatIdentity yet: no brand.
        await people.RunActionAsync("sub-anna", "invite", null);
        Assert.Null(handler.Brands.Single());

        await config.SetIdentityAsync(new IdentitySignIn { Authority = "http://matidentity.test", ClientId = "matos-home" }, "secret");
        await people.RunActionAsync("sub-anna", "resetPassword", null);
        Assert.Equal("matos-home", handler.Brands[^1]);

        // A read names none.
        handler.Answer = _ => (HttpStatusCode.OK, Anna);
        await people.PersonAsync("sub-anna");
        Assert.Null(handler.Brands[^1]);
    }

    private static MatIdentityPeople People(ScimHandler handler) => new(new StubLocator(), new StubFactory(handler));

    private sealed class ScimHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Key, string? Actor, JsonNode? Body)> Requests { get; } = [];

        public List<string?> Brands { get; } = [];

        public Func<(string Method, string Url), (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

        public string ContentType { get; set; } = "application/scim+json";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.AbsoluteUri;
            Brands.Add(request.Headers.TryGetValues(MatIdentityPeople.BrandHeader, out var brand) ? brand.Single() : null);
            Requests.Add((request.Method.Method, url, request.Headers.TryGetValues("X-Api-Key", out var key) ? key.Single() : null,
                request.Headers.TryGetValues(MatIdentityPeople.ActorHeader, out var actor) ? actor.Single() : null,
                string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var (status, body) = Answer((request.Method.Method, url));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, ContentType) };
            if ((int)status is >= 300 and < 400)
            {
                response.Headers.Location = new Uri("http://matidentity.test/Account/Login?returnUrl=%2Fscim%2Fv2");
            }

            return response;
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubLocator : EngineLocator
    {
        public StubLocator()
            : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://matidentity.test", "the-key"));
    }
}
