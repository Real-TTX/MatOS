using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core.Auth;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MatOS.Tests;

/// <summary>
/// The back-channel logout (OpenID Connect Back-Channel Logout 1.0): the address MatOS registers in MatIdentity for each app and for itself, the check of a
/// logout token (<see cref="LogoutToken"/>) and the end of the sessions (<see cref="SessionService.EndIdentitySessionsAsync"/>).
/// </summary>
public class BackchannelLogoutTests
{
    private const string Issuer = "https://id.example.com/";
    private const string ClientId = "matos";

    private static readonly SigningCredentials Credentials = NewCredentials("key-1");

    // ------------------------------------------------------------------ the addresses

    [Fact]
    public void MatOS_is_called_at_its_own_container_and_not_at_all_outside_docker()
    {
        Assert.Equal("http://matos:8030/signout-backchannel", IdentityLink.SelfBackchannelLogoutUriOf("matos", 8030));
        Assert.Equal(string.Empty, IdentityLink.SelfBackchannelLogoutUriOf(null, 8030));
    }

    [Fact]
    public void The_catalog_entry_has_the_default_path_and_may_change_it()
    {
        const string Entry = """
            x-matos:
              id: demo
              name: Demo
              ui:
                service: web
                port: 8080
              identity: {}
            services:
              web:
                image: nginx
            """;
        Assert.Equal("/signout-backchannel", CatalogParser.Parse(Entry, "test").Identity!.Backchannel);
        Assert.Equal("/auth/bc", CatalogParser.Parse(Entry.Replace("identity: {}", "identity: { backchannel: /auth/bc }"), "test").Identity!.Backchannel);
    }

    [Theory]
    [InlineData("http://matos-matsplit-web:8080/signout-backchannel")]
    [InlineData("")]
    public async Task The_client_is_put_with_its_backchannel_logout_address(string address)
    {
        var handler = new StubHandler();
        await new MatIdentityClient(new StubLocator(), new StubFactory(handler))
            .UpsertClientAsync("matsplit", "MatSplit", "https://matsplit.example.com", ["https://matsplit.example.com/signin-oidc"], [], false, false, address, default);

        var body = Assert.Single(handler.Bodies)!;
        Assert.Equal(address, body["backchannelLogoutUri"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_an_address_the_field_is_left_out_and_MatIdentity_keeps_what_it_has()
    {
        var handler = new StubHandler();
        await new MatIdentityClient(new StubLocator(), new StubFactory(handler))
            .UpsertClientAsync("matsplit", "MatSplit", null, [], [], false, false, null, default);

        Assert.False(Assert.Single(handler.Bodies)!.AsObject().ContainsKey("backchannelLogoutUri"));
    }

    // ------------------------------------------------------------------ the logout token

    [Fact]
    public async Task A_valid_token_names_the_person_and_the_session()
    {
        var check = await Validate(Token());

        Assert.True(check.IsValid, check.Error);
        Assert.Equal(new LogoutToken("person-1", "session-1"), check.Token);
    }

    [Fact]
    public async Task A_token_with_the_subject_alone_is_valid()
        => Assert.Equal(new LogoutToken("person-1", null), (await Validate(Token(c => c.Remove("sid"))))!.Token);

    [Theory]
    [InlineData("another key", "not valid")]
    [InlineData("another issuer", "issuer")]
    [InlineData("another audience", "udience")]
    [InlineData("issued long ago", "too old")]
    [InlineData("issued in the future", "future")]
    [InlineData("no iat", "no iat")]
    [InlineData("expired", "IDX10223")]
    [InlineData("no events", "events")]
    [InlineData("another event", "events")]
    [InlineData("neither sub nor sid", "neither sub nor sid")]
    [InlineData("a nonce", "nonce")]
    [InlineData("no jti", "no jti")]
    [InlineData("an ID token type", "type JWT")]
    [InlineData("not signed", "not valid")]
    [InlineData("nothing", "missing")]
    public async Task An_invalid_token_is_refused(string kind, string reason)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = kind switch
        {
            "another key" => Token(credentials: NewCredentials("key-1")),
            "another issuer" => Token(c => c["iss"] = "https://other.example.com/"),
            "another audience" => Token(c => c["aud"] = "matsplit"),
            "issued long ago" => Token(c => c["iat"] = now - 3600),
            "issued in the future" => Token(c => c["iat"] = now + 3600),
            "no iat" => Token(c => c.Remove("iat")),
            "expired" => Token(c => { c["nbf"] = now - 900; c["exp"] = now - 600; }),
            "no events" => Token(c => c.Remove("events")),
            "another event" => Token(c => c["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/other"] = new Dictionary<string, object>() }),
            "neither sub nor sid" => Token(c => { c.Remove("sub"); c.Remove("sid"); }),
            "a nonce" => Token(c => c["nonce"] = "n-1"),
            "no jti" => Token(c => c.Remove("jti")),
            "an ID token type" => Token(type: "JWT"),
            "not signed" => Base64UrlEncoder.Encode("""{"alg":"none","typ":"logout+jwt"}""") + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(Claims())) + ".",
            _ => string.Empty,
        };

        var check = await Validate(token);

        Assert.False(check.IsValid);
        Assert.Contains(reason, check.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_is_accepted_once()
    {
        var replay = new LogoutReplayCache();
        var token = Token();

        Assert.True((await Validate(token, replay)).IsValid);
        var again = await Validate(token, replay);
        Assert.False(again.IsValid);
        Assert.Contains("used before", again.Error);
    }

    [Fact]
    public async Task A_new_key_of_MatIdentity_is_read_again()
    {
        var rotated = NewCredentials("key-2");
        var manager = new RotatingManager(Document(Credentials), Document(rotated));

        var check = await LogoutToken.ValidateAsync(Token(credentials: rotated), manager, ClientId, new LogoutReplayCache(), DateTimeOffset.UtcNow, default);

        Assert.True(check.IsValid, check.Error);
        Assert.True(manager.Refreshed);
    }

    // ------------------------------------------------------------------ the sessions

    [Fact]
    public async Task A_sid_ends_that_session_and_the_subject_alone_ends_all_of_the_person()
    {
        var file = Path.Combine(Path.GetTempPath(), "matos-bcl-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new MatOSDbContext(new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + file).Options, new SystemCurrentUserAccessor());
            await db.Database.MigrateAsync();
            var anna = new User { Username = "anna", ExternalSubject = "person-1" };
            var ben = new User { Username = "ben", ExternalSubject = "person-2" };
            db.Users.AddRange(anna, ben);
            await db.SaveChangesAsync();

            var sessions = new SessionService(db, Microsoft.Extensions.Options.Options.Create(new MatOSOptions()));
            var laptop = await sessions.CreateAsync(anna.Id, "laptop", null, "session-1");
            var phone = await sessions.CreateAsync(anna.Id, "phone", null, "session-2");
            var local = await sessions.CreateAsync(anna.Id, "local", null);
            var other = await sessions.CreateAsync(ben.Id, "ben", null, "session-3");

            Assert.Equal(1, await sessions.EndIdentitySessionsAsync("person-1", "session-1"));
            Assert.Null(await sessions.ValidateAsync(laptop.Token));
            Assert.NotNull(await sessions.ValidateAsync(phone.Token));
            Assert.NotNull(await sessions.ValidateAsync(local.Token));

            // The sid of another person ends nothing; an unknown subject neither.
            Assert.Equal(0, await sessions.EndIdentitySessionsAsync("person-1", "session-3"));
            Assert.Equal(0, await sessions.EndIdentitySessionsAsync("nobody", null));
            Assert.NotNull(await sessions.ValidateAsync(other.Token));

            // Signed out everywhere: every session of the person, the local one too.
            Assert.Equal(2, await sessions.EndIdentitySessionsAsync("person-1", null));
            Assert.Null(await sessions.ValidateAsync(phone.Token));
            Assert.Null(await sessions.ValidateAsync(local.Token));
            Assert.NotNull(await sessions.ValidateAsync(other.Token));

            // A sid alone.
            Assert.Equal(1, await sessions.EndIdentitySessionsAsync(null, "session-3"));
            Assert.Null(await sessions.ValidateAsync(other.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_session_without_a_sid_ends_only_when_the_person_is_signed_out_everywhere()
    {
        var file = Path.Combine(Path.GetTempPath(), "matos-bcl-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new MatOSDbContext(new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + file).Options, new SystemCurrentUserAccessor());
            await db.Database.MigrateAsync();
            var anna = new User { Username = "anna", ExternalSubject = "person-1" };
            db.Users.Add(anna);
            await db.SaveChangesAsync();

            var sessions = new SessionService(db, Microsoft.Extensions.Options.Options.Create(new MatOSOptions()));
            var old = await sessions.CreateAsync(anna.Id, "old", null);

            Assert.Equal(0, await sessions.EndIdentitySessionsAsync("person-1", "session-9"));
            Assert.NotNull(await sessions.ValidateAsync(old.Token));
            Assert.Equal(1, await sessions.EndIdentitySessionsAsync("person-1", null));
            Assert.Null(await sessions.ValidateAsync(old.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static SigningCredentials NewCredentials(string keyId)
        => new(new RsaSecurityKey(RSA.Create(2048)) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);

    private static OpenIdConnectConfiguration Document(SigningCredentials credentials)
    {
        var document = new OpenIdConnectConfiguration { Issuer = Issuer };
        document.SigningKeys.Add(credentials.Key);
        return document;
    }

    private static Task<LogoutTokenCheck> Validate(string token, LogoutReplayCache? replay = null)
        => LogoutToken.ValidateAsync(token, new StaticConfigurationManager<OpenIdConnectConfiguration>(Document(Credentials)), ClientId, replay ?? new LogoutReplayCache(), DateTimeOffset.UtcNow, default);

    /// <summary>A logout token as MatIdentity writes it, changed by <paramref name="change"/>.</summary>
    private static string Token(Action<Dictionary<string, object>>? change = null, SigningCredentials? credentials = null, string type = LogoutToken.TokenType)
    {
        var claims = Claims();
        change?.Invoke(claims);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = type,
            SigningCredentials = credentials ?? Credentials,
        });
    }

    private static Dictionary<string, object> Claims()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = ClientId,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["sub"] = "person-1",
            ["sid"] = "session-1",
            ["events"] = new Dictionary<string, object> { [LogoutToken.EventType] = new Dictionary<string, object>() },
        };
    }

    /// <summary>A discovery document that has the new key only after a refresh.</summary>
    private sealed class RotatingManager(OpenIdConnectConfiguration before, OpenIdConnectConfiguration after) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public bool Refreshed { get; private set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(Refreshed ? after : before);

        public void RequestRefresh() => Refreshed = true;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<JsonNode?> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"client":{}}""", Encoding.UTF8, "application/json") };
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
