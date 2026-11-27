using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using MatOS.Core.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// The hand-over of the person who set up MatOS to MatIdentity (<see cref="OwnerHandover"/>) keeps how it ended for the screen "Your account in MatIdentity":
/// a new person (created), a person MatIdentity knew (linked, and made an administrator there, so a sign-in through MatIdentity keeps the role), or a password
/// that could not be read any more. MatIdentity is a fake SCIM endpoint.
/// </summary>
public sealed class OwnerHandoverTests
{
    private const string Email = "anna@example.com";

    [Fact]
    public async Task A_new_person_is_created_and_the_outcome_is_kept()
    {
        var scim = new FakeScim();
        await using var setup = await Setup.CreateAsync(scim, "blue-river-42");

        Assert.True(await setup.Handover.HandOverAsync(_ => { }, CancellationToken.None));

        var config = await setup.Config.GetAsync();
        Assert.Null(config.Owner);
        Assert.Equal(HandOverOutcome.Created, config.HandOver?.Outcome);
        Assert.Equal(Email, config.HandOver?.Email);
        var created = scim.Requests.Single(r => r.Method == "POST");
        Assert.Equal("blue-river-42", created.Body?["password"]?.GetValue<string>());
        Assert.True(created.Body?[MatIdentityPeople.UserAccess]?["administrator"]?.GetValue<bool>());
        Assert.DoesNotContain(scim.Requests, r => r.Method == "PATCH");
        Assert.Equal("sub-new", (await setup.UserAsync()).ExternalSubject);
    }

    [Fact]
    public async Task An_existing_person_who_is_no_administrator_is_made_one_and_linked()
    {
        var scim = new FakeScim { Existing = Person("sub-anna", administrator: false) };
        await using var setup = await Setup.CreateAsync(scim, "blue-river-42");
        var lines = new List<string>();

        Assert.True(await setup.Handover.HandOverAsync(lines.Add, CancellationToken.None));

        Assert.Equal(HandOverOutcome.Linked, (await setup.Config.GetAsync()).HandOver?.Outcome);
        Assert.DoesNotContain(scim.Requests, r => r.Method == "POST");
        var patch = scim.Requests.Single(r => r.Method == "PATCH");
        Assert.EndsWith("/scim/v2/Users/sub-anna", patch.Url, StringComparison.Ordinal);
        var operations = patch.Body!["Operations"]!.AsArray();
        Assert.Contains(operations, o => o?["path"]?.GetValue<string>() == MatIdentityPeople.UserAccess + ":administrator" && o?["value"]?.GetValue<bool>() == true);
        Assert.DoesNotContain(operations, o => o?["path"]?.GetValue<string>() == "password");
        Assert.Contains(lines, l => l.Contains("administrator", StringComparison.Ordinal));
        Assert.Equal("sub-anna", (await setup.UserAsync()).ExternalSubject);
    }

    [Fact]
    public async Task An_existing_administrator_is_only_linked()
    {
        var scim = new FakeScim { Existing = Person("sub-anna", administrator: true) };
        await using var setup = await Setup.CreateAsync(scim, "blue-river-42");

        Assert.True(await setup.Handover.HandOverAsync(_ => { }, CancellationToken.None));

        Assert.Equal(HandOverOutcome.Linked, (await setup.Config.GetAsync()).HandOver?.Outcome);
        Assert.DoesNotContain(scim.Requests, r => r.Method is "PATCH" or "POST");
    }

    [Fact]
    public async Task A_lost_password_is_kept_as_such_and_nobody_waits_any_more()
    {
        var scim = new FakeScim();
        await using var setup = await Setup.CreateAsync(scim, password: null);

        Assert.False(await setup.Handover.HandOverAsync(_ => { }, CancellationToken.None));

        var config = await setup.Config.GetAsync();
        Assert.Null(config.Owner);
        Assert.Equal(HandOverOutcome.PasswordLost, config.HandOver?.Outcome);
        Assert.Empty(scim.Requests);
    }

    [Fact]
    public void An_administrator_keeps_the_details_and_gets_no_new_password()
    {
        var person = new IdentityPerson("sub-anna", "anna", "Anna Berg", Email, Active: true, Administrator: false);
        var input = OwnerHandover.AdministratorOf(person);
        Assert.Equal(new IdentityPersonInput("anna", "Anna Berg", Email, true, true, null), input);
    }

    private static string Person(string subject, bool administrator) => $$"""
        { "schemas": ["{{MatIdentityPeople.UserSchema}}", "{{MatIdentityPeople.UserAccess}}"], "id": "{{subject}}", "userName": "{{Email}}", "displayName": "Anna",
          "active": true, "emails": [ { "value": "{{Email}}", "primary": true } ],
          "{{MatIdentityPeople.UserAccess}}": { "administrator": {{(administrator ? "true" : "false")}} } }
        """;

    /// <summary>The config, the database with the account of the first start, the fake MatIdentity and the hand-over.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        private readonly string _root;

        private Setup(TestDatabase database, string root, SystemConfigService config, OwnerHandover handover, long userId)
        {
            _database = database;
            _root = root;
            Config = config;
            Handover = handover;
            UserId = userId;
        }

        public SystemConfigService Config { get; }

        public OwnerHandover Handover { get; }

        public long UserId { get; }

        public static async Task<Setup> CreateAsync(FakeScim scim, string? password)
        {
            var root = Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"));
            var config = new SystemConfigService(new JsonConfigStore(new AppPaths(root), NullLogger<JsonConfigStore>.Instance), new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
            var database = await TestDatabase.CreateAsync();
            var user = new User { Username = Email, DisplayName = "Anna", Role = UserRole.Admin };
            database.Db.Users.Add(user);
            await database.Db.SaveChangesAsync();
            await config.SetOwnerAsync(new PendingOwner { UserId = user.Id, Username = Email, DisplayName = "Anna", Email = Email }, password);
            var people = new MatIdentityPeople(new StubLocator(), new StubFactory(scim));
            return new Setup(database, root, config, new OwnerHandover(config, people, database.Db, NullLogger<OwnerHandover>.Instance), user.Id);
        }

        public async Task<User> UserAsync()
        {
            _database.Db.ChangeTracker.Clear();
            return await _database.Db.Users.SingleAsync(u => u.Id == UserId);
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    /// <summary>MatIdentity's SCIM: the list of people (empty, or the one that exists), a new person, a change of one.</summary>
    private sealed class FakeScim : HttpMessageHandler
    {
        public string? Existing { get; init; }

        public List<(string Method, string Url, JsonNode? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = request.Method.Method;
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add((method, url, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var body = method switch
            {
                "GET" => Existing is null ? """{ "totalResults": 0, "Resources": [] }""" : $$"""{ "totalResults": 1, "Resources": [ {{Existing}} ] }""",
                "POST" => Person("sub-new", administrator: true),
                "PATCH" => Person(url[(url.LastIndexOf('/') + 1)..], administrator: true),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/scim+json") };
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
