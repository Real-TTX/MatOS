using System.Net;
using System.Text;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Tests;

/// <summary>
/// The home screen per person (MatDesign docs/ACCESS.md "What each person sees"): which apps a person may use, who sees a launcher or widget by the person's role in the
/// app, and how long MatOS keeps what MatIdentity said.
/// </summary>
public sealed class PersonAccessTests
{
    private static InstalledApp App(string slug) => new() { Slug = slug, Name = slug };

    [Fact]
    public void A_person_may_use_the_apps_matidentity_lets_them_in_and_every_app_without_it()
    {
        var person = new PersonApps(
            new Dictionary<string, string?> { ["matdrive"] = "user", ["x-app"] = null },
            new HashSet<string> { "matdrive", "matdo", "x-app" },
            new Dictionary<string, string?> { ["matdrive"] = "admin", ["matdo"] = "admin", ["x-app"] = null });

        Assert.True(person.MayUse(App("matdrive")));
        Assert.False(person.MayUse(App("matdo")));
        // An app that does not sign in through MatIdentity has users of its own: it stays on the screen.
        Assert.True(person.MayUse(App("portainer")));
        // A one-letter slug is a client id with "-app", as MatIdentity names it.
        Assert.True(person.MayUse(App("x")));
        Assert.Equal("user", person.RoleIn(App("matdrive")));
        Assert.Equal("admin", person.FirstRoleOf(App("matdrive")));
        Assert.Null(person.RoleIn(App("x")));
    }

    [Theory]
    // Without the person's role the old rules: everyone, administrators of MatOS, a launcher for the person.
    [InlineData("admin", null, false, true, null, "admin", false)]
    [InlineData("admin", null, true, false, null, "admin", true)]
    // With the role: admin is the app's first role, not the administrator of MatOS.
    [InlineData("admin", null, false, false, "admin", "admin", true)]
    [InlineData("admin", null, true, false, "user", "admin", false)]
    // An app without roles of MatIdentity's: admin stays the administrator of MatOS.
    [InlineData("admin", null, true, false, "member", null, true)]
    [InlineData(null, null, false, false, "user", "admin", true)]
    // roles decide alone when the role is known.
    [InlineData("admin", "admin,verwalter", false, false, "verwalter", "admin", true)]
    [InlineData("admin", "admin,verwalter", true, false, "user", "admin", false)]
    // A widget with the values of the person is not shown yet; a launcher for the person is.
    [InlineData("person", null, false, false, "user", "admin", false)]
    [InlineData("person", null, false, true, "user", "admin", true)]
    public void Who_sees_a_contribution_by_the_role_in_the_app(string? audience, string? roles, bool isAdmin, bool launcher, string? role, string? firstRole, bool sees)
        => Assert.Equal(sees, ShellRules.Sees(audience, roles?.Split(','), isAdmin, launcher, role, firstRole));

    [Fact]
    public async Task What_matidentity_said_is_kept_a_minute_and_forgotten_after_a_change()
    {
        var handler = new Handler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var access = new PersonAccess(new MatIdentityPeople(new StubLocator(), new StubFactory(handler)), null!, cache);

        var first = await access.ForAsync("sub-anna");
        Assert.NotNull(first);
        Assert.True(first.MayUse(App("matdrive")));
        Assert.False(first.MayUse(App("matdo")));
        var asked = handler.Count;

        await access.ForAsync("sub-anna");
        Assert.Equal(asked, handler.Count);

        access.Forget("sub-anna");
        await access.ForAsync("sub-anna");
        Assert.True(handler.Count > asked);

        asked = handler.Count;
        PersonAccess.ForgetEveryone();
        await access.ForAsync("sub-anna");
        Assert.True(handler.Count > asked);

        // A local account and a person MatIdentity does not know: nothing to say.
        Assert.Null(await access.ForAsync(null));
        Assert.Null(await access.ForAsync("sub-nobody"));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("html")]
    [InlineData("down")]
    [InlineData("slow")]
    public async Task A_matidentity_without_scim_or_without_an_answer_never_takes_the_home_screen_down(string kind)
    {
        var handler = new Failing(kind);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var access = new PersonAccess(new MatIdentityPeople(new StubLocator(), new StubFactory(handler)), null!, cache);

        // Nothing to say: the home screen shows what it always showed.
        Assert.Null(await access.ForAsync("sub-anna"));

        // And it is kept like an answer: a MatIdentity that is down is not asked at every look.
        var asked = handler.Count;
        Assert.Null(await access.ForAsync("sub-anna"));
        Assert.Equal(asked, handler.Count);
    }

    private sealed class Failing(string kind) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            switch (kind)
            {
                case "redirect":
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent(string.Empty) };
                    redirect.Headers.Location = new Uri("http://matidentity.test/Account/Login");
                    return redirect;
                case "html":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html") };
                case "down":
                    throw new HttpRequestException("Connection refused");
                default:
                    // Longer than the home screen waits: the wait cancels it.
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var path = request.RequestUri!.AbsolutePath;
            var (status, body) = path switch
            {
                "/scim/v2/Users/sub-anna" => (HttpStatusCode.OK, """
                    { "id": "sub-anna", "userName": "anna", "urn:mat:scim:schemas:extension:access:1.0:User": { "access": [ { "app": "matdrive", "role": "user", "permissions": [], "quotas": {}, "via": ["person"] } ] } }
                    """),
                "/api/v1/clients" => (HttpStatusCode.OK, """
                    [ { "clientId": "matdrive", "allowSignIn": true, "roleMode": "matidentity", "roles": [ { "key": "admin" }, { "key": "user" } ] },
                      { "clientId": "matdo", "allowSignIn": true, "roleMode": "matidentity", "roles": [] } ]
                    """),
                _ => (HttpStatusCode.NotFound, """{ "detail": "no" }"""),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/scim+json") });
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
