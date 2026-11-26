using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// The engines and the mail through MatSMTP (MatDesign docs/PLATFORM.md, "Mail through MatSMTP", "`mail.smtp`"): the five engines and their order, `consumes: mail.smtp`
/// in the catalog (and the old key `mail: {}` read into it), the entry of MatSMTP, the apps that send mail and <see cref="MatSmtpClient"/> against a stub. The relay users
/// are bindings of the broker now (BindingTests).
/// </summary>
public class MailTests
{
    private const string Minimal = """
        x-matos:
          id: demo
          name: Demo
          mail: {}
        services:
          web:
            image: nginx
        """;

    private static readonly string[] MailApps = ["matdock", "matbu", "matmon", "matcms", "matddns", "matidentity", "matdrive", "matoffice", "matboard", "matshape"];

    private static CatalogService CreateCatalog()
        => new(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    // ------------------------------------------------------------------ the engines

    [Fact]
    public void The_five_engines_are_installed_MatDock_first_and_MatSMTP_after_MatIdentity()
    {
        Assert.Equal(["matdock", "matcad", "matddns", "matidentity", "matsmtp"], EngineIds.All);
        Assert.True(EngineIds.IsEngine("matsmtp"));
        Assert.True(EngineIds.IsEngine("matidentity"));
        Assert.False(EngineIds.IsEngine("matsplit"));
        Assert.False(EngineIds.IsEngine(null));
        Assert.Equal(EngineIds.MatIdentity, MatIdentityClient.CatalogIdValue);
        Assert.Equal(EngineIds.MatSmtp, MatSmtpClient.CatalogIdValue);
    }

    [Fact]
    public void Every_engine_is_a_core_entry_of_the_catalog_with_a_provisioner_and_an_icon()
    {
        var catalog = CreateCatalog();
        foreach (var id in EngineIds.All)
        {
            var app = catalog.Get(id);
            Assert.NotNull(app);
            Assert.True(app.Core, $"{id}: core");
            Assert.Equal(id, app.Provision);
            Assert.Equal(id + ".svg", app.Icon);
        }

        // Nothing else is an engine.
        Assert.All(catalog.GetAll().Where(a => a.Core), a => Assert.True(EngineIds.IsEngine(a.Id), a.Id));
    }

    [Fact]
    public void MatSMTP_is_in_the_catalog_as_the_mail_relay()
    {
        var smtp = CreateCatalog().Get(MatSmtpClient.CatalogIdValue);
        Assert.NotNull(smtp);
        Assert.Equal(AppRoles.Mail, smtp.Role);
        Assert.Equal("Infrastructure", smtp.Category);
        Assert.True(smtp.Platform);
        Assert.True(smtp.Uses(Capabilities.IdentityOidc));
        Assert.False(smtp.Uses(Capabilities.MailSmtp));
        Assert.Equal("matsmtp", smtp.Ui!.Service);
        Assert.Equal(2525, smtp.Ui.Port);
        Assert.Equal(2525, smtp.Ui.HostPort);

        var key = Assert.Single(smtp.Variables, v => v.Key == MatSmtpClient.KeyVariableName);
        Assert.True(key.Hidden && key.Generate && key.IsSecret);
        var admin = Assert.Single(smtp.Variables, v => v.Key == "ADMIN_PASSWORD");
        Assert.True(admin.Generate && admin.IsSecret);
        Assert.Contains(smtp.Variables, v => v.Key == ManagedVariables.ApiKey && v.Hidden && v.Generate);

        Assert.Contains("MATSMTP_API_KEY: ${MATSMTP_API_KEY}", smtp.ComposeYaml);
        Assert.Contains("MATSMTP_ADMIN_PASSWORD: ${ADMIN_PASSWORD}", smtp.ComposeYaml);
        Assert.Contains("- matsmtp-data:/data", smtp.ComposeYaml);

        // The apps hand in their mail on the shared network: the SMTP port of the container (2025) is never published, only the submission port for devices.
        var published = Assert.Single(smtp.FixedPorts);
        Assert.Equal(2587, published.HostPort);
        Assert.DoesNotContain(smtp.FixedPorts, p => p.HostPort is 25 or 2025);
    }

    [Fact]
    public void MatIdentity_is_an_engine_now()
    {
        var identity = CreateCatalog().Get(MatIdentityClient.CatalogIdValue);
        Assert.NotNull(identity);
        Assert.True(identity.Core);
    }

    // ------------------------------------------------------------------ mail.smtp in the catalog

    [Fact]
    public void The_old_key_mail_is_read_as_the_capability_and_brings_no_variables_any_more()
    {
        var app = CatalogParser.Parse(Minimal, "test");

        var mail = Assert.Single(app.Consumes);
        Assert.Equal(Capabilities.MailSmtp, mail.Capability);
        Assert.Equal(1, mail.Version);
        Assert.Empty(mail.Params);

        // The variables of the binding are the broker's: none of them is a variable of the entry.
        Assert.DoesNotContain(app.Variables, v => v.Key.StartsWith("MATSMTP_", StringComparison.Ordinal));
        Assert.Equal(["MATSMTP_HOST", "MATSMTP_PORT", "MATSMTP_USERNAME", "MATSMTP_PASSWORD", "MATSMTP_FROM", "MATSMTP_TLS"], ManagedVariables.Mail);
    }

    [Fact]
    public void Mail_needs_no_web_service_and_no_platform()
    {
        var app = CatalogParser.Parse(Minimal, "test");
        Assert.Null(app.Ui);
        Assert.DoesNotContain(app.Variables, v => v.Key == ManagedVariables.ApiKey);
    }

    [Fact]
    public void The_sender_is_a_parameter_and_must_be_the_part_before_the_at()
    {
        Assert.Equal("noreply", CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: noreply"), "test").Consumes.Single().Params["sender"]);
        var generic = Minimal.Replace("  mail: {}", "  consumes:\n    - capability: mail.smtp\n      params:\n        sender: shop");
        Assert.Equal("shop", CatalogParser.Parse(generic, "test").Consumes.Single().Params["sender"]);
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: a@b.com"), "test"));
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: \"Bad Name\""), "test"));
    }

    [Fact]
    public void A_capability_must_have_a_name_like_area_dot_verb()
    {
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("  mail: {}", "  consumes:\n    - capability: mail"), "test"));
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("  mail: {}", "  consumes:\n    - capability: mail.smtp\n      version: 0"), "test"));
    }

    [Fact]
    public void The_old_key_and_the_new_list_say_the_capability_once()
    {
        var both = Minimal.Replace("  mail: {}", "  mail: {}\n  consumes:\n    - capability: mail.smtp\n      params:\n        sender: shop");
        var mail = Assert.Single(CatalogParser.Parse(both, "test").Consumes);
        Assert.Equal("shop", mail.Params["sender"]);
    }

    [Theory]
    [InlineData("matdock")]
    [InlineData("matbu")]
    [InlineData("matmon")]
    [InlineData("matcms")]
    [InlineData("matddns")]
    [InlineData("matidentity")]
    [InlineData("matdrive")]
    [InlineData("matoffice")]
    public void The_app_sends_its_mail_through_MatSMTP(string id)
    {
        var app = CreateCatalog().Get(id);
        Assert.NotNull(app);
        Assert.True(app.Uses(Capabilities.MailSmtp));
        Assert.Null(app.Mail);
    }

    [Fact]
    public void Only_the_apps_that_send_mail_say_so()
        => Assert.Equal(MailApps.Order(), CreateCatalog().GetAll().Where(a => a.Uses(Capabilities.MailSmtp)).Select(a => a.Id).Order());

    // ------------------------------------------------------------------ MatSmtpClient against a stub

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _answers = new();

        public List<(HttpMethod Method, string Path, string Query, string? Key, JsonNode? Body)> Requests { get; } = new();

        public StubHandler Answer(string method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers[$"{method} {path}"] = (status, json);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/", string.Empty);
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, request.RequestUri.Query, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null,
                string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var (status, json) = _answers.TryGetValue($"{request.Method} {path}", out var answer) ? answer : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

        public string? AskedFor { get; private set; }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
        {
            AskedFor = $"{catalogId} {keyVariable}";
            return Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://matsmtp.test", "the-key"));
        }
    }

    [Fact]
    public async Task The_client_reads_the_status_with_the_key_of_MatSMTP()
    {
        var handler = new StubHandler().Answer("GET", "status", """{"app":"matsmtp","version":"1.4.0","apiVersion":1,"smtpPort":2025,"smtpTls":"STARTTLS"}""");
        var locator = new StubLocator();
        var status = await new MatSmtpClient(locator, new StubFactory(handler)).GetSmtpStatusAsync();

        Assert.Equal(new MatSmtpStatus("1.4.0", 1, 2025, "starttls"), status);
        Assert.Equal("matsmtp MATSMTP_API_KEY", locator.AskedFor);
        Assert.Equal("the-key", handler.Requests[0].Key);
    }

    [Theory]
    [InlineData("""{"version":"1","apiVersion":1}""", 2025, "none")]
    [InlineData("""{"version":"1","apiVersion":1,"smtpPort":0,"smtpTls":"ssl"}""", 2025, "none")]
    [InlineData("""{"version":"1","apiVersion":1,"smtpPort":2465,"smtpTls":"tls"}""", 2465, "tls")]
    public void A_status_without_a_port_or_with_an_unknown_tls_mode_has_the_defaults(string json, int port, string tls)
    {
        var status = MatSmtpClient.ParseStatus(JsonNode.Parse(json));
        Assert.Equal(port, status!.SmtpPort);
        Assert.Equal(tls, status.SmtpTls);
    }

    [Fact]
    public async Task A_MatSMTP_that_does_not_answer_has_no_status()
    {
        var handler = new StubHandler().Answer("GET", "status", """{"error":"The API is off."}""", HttpStatusCode.ServiceUnavailable);
        Assert.Null(await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).GetSmtpStatusAsync());
    }

    [Fact]
    public async Task The_relay_user_is_put_with_the_marker_and_the_password_is_read()
    {
        var handler = new StubHandler().Answer("PUT", "relay-users/matbu", """{"id":"matbu","username":"matbu","password":"s3cret","created":true}""");
        var user = await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).UpsertRelayUserAsync("matbu", "MatBu", ["matbu@example.com"], renewSecret: false, default);

        Assert.Equal(new RelayUser("matbu", "matbu", "s3cret", true), user);
        var (method, _, _, _, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("MatBu", body!["displayName"]!.GetValue<string>());
        Assert.Equal("matbu@example.com", body["allowedSenders"]![0]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        Assert.False(body["renewSecret"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_existing_user_comes_back_without_a_password()
    {
        var handler = new StubHandler().Answer("PUT", "relay-users/matbu", """{"id":"matbu","username":"matbu","created":false}""");
        var user = await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).UpsertRelayUserAsync("matbu", "MatBu", ["matbu@example.com"], renewSecret: false, default);
        Assert.Null(user.Password);
        Assert.False(user.Created);
    }

    [Fact]
    public async Task Deleting_names_the_marker_and_a_missing_user_is_no_error()
    {
        var handler = new StubHandler().Answer("DELETE", "relay-users/gone", """{"error":"Not found."}""", HttpStatusCode.NotFound);
        var client = new MatSmtpClient(new StubLocator(), new StubFactory(handler));

        await client.DeleteRelayUserAsync("gone", default);
        Assert.Equal("?managedBy=matos", handler.Requests[0].Query);
    }

    [Fact]
    public async Task A_user_MatOS_did_not_make_is_not_deleted()
    {
        var handler = new StubHandler().Answer("DELETE", "relay-users/shop", """{"error":"Not managed by matos."}""", HttpStatusCode.Conflict);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MatSmtpClient(new StubLocator(), new StubFactory(handler)).DeleteRelayUserAsync("shop", default));
        Assert.Contains("not made by MatOS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_are_read_and_written()
    {
        var handler = new StubHandler()
            .Answer("GET", "settings", """{"setupCompleted":false,"defaultSenderDomain":""}""")
            .Answer("PUT", "settings", """{"setupCompleted":true,"defaultSenderDomain":"example.com"}""");
        var client = new MatSmtpClient(new StubLocator(), new StubFactory(handler));

        Assert.Equal(new MatSmtpRelaySettings(false, null), await client.GetSettingsAsync(default));
        Assert.Equal(new MatSmtpRelaySettings(true, "example.com"), await client.UpdateSettingsAsync(new MatSmtpRelaySettings(true, "example.com"), default));
        var body = handler.Requests[1].Body!;
        Assert.True(body["setupCompleted"]!.GetValue<bool>());
        Assert.Equal("example.com", body["defaultSenderDomain"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_relay_users_of_MatOS_are_listed()
    {
        var handler = new StubHandler().Answer("GET", "relay-users", """[{"id":"matbu","managedBy":"matos"},{"id":"printer"},{"id":"matdock","managedBy":"matos"}]""");
        Assert.Equal(["matbu", "matdock"], await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).ListRelayUsersAsync(default));
    }
}
