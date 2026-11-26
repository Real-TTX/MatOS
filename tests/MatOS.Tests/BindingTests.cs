using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

/// <summary>The broker of the app model (MatDesign docs/PLATFORM.md "Bindings") and the shell side of the contributions: the decisions without Docker or HTTP.</summary>
public class BindingTests
{
    private static readonly IOptions<MatOSOptions> Options = Microsoft.Extensions.Options.Options.Create(new MatOSOptions());

    private static ComposeRenderer Renderer() => new(Options);

    private static string ReadCatalog(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static CatalogApp Entry(string id) => CatalogParser.Parse(ReadCatalog(id), id + ".yml");

    private static InstalledApp AppOf(string slug, string uiService, int uiPort)
        => new() { Id = 7, Slug = slug, Name = slug, ProjectName = "matos-" + slug, UiService = uiService, UiPort = uiPort, HostPort = uiPort };

    private static IDictionary<object, object> Service(string yaml, string name)
    {
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
        return (IDictionary<object, object>)((IDictionary<object, object>)compose["services"])[name];
    }

    private static Dictionary<string, JsonElement> Answer(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Merge_takes_the_values_and_keeps_a_secret_that_was_not_handed_out_again()
    {
        var stored = new Dictionary<string, string> { ["MAT_OFFICE_EDIT_KEY"] = "mo_kept" };
        var merged = BindingBroker.Merge(Answer("""{"MAT_OFFICE_EDIT_URL": "https://office.example.com", "MAT_OFFICE_EDIT_INTERNAL_URL": "", "MAT_OFFICE_EDIT_KEY": {"isSet": true}}"""), stored)!;
        Assert.Equal("https://office.example.com", merged["MAT_OFFICE_EDIT_URL"]);
        Assert.Equal(string.Empty, merged["MAT_OFFICE_EDIT_INTERNAL_URL"]);
        Assert.Equal("mo_kept", merged["MAT_OFFICE_EDIT_KEY"]);

        // A secret the provider keeps but MatOS lost: the binding must be renewed.
        Assert.Null(BindingBroker.Merge(Answer("""{"MAT_OFFICE_EDIT_KEY": {"isSet": true}}"""), new Dictionary<string, string>()));

        // A new secret replaces the stored one; anything that is not a text is empty.
        var renewed = BindingBroker.Merge(Answer("""{"MAT_OFFICE_EDIT_KEY": "mo_new", "OTHER": 3}"""), stored)!;
        Assert.Equal("mo_new", renewed["MAT_OFFICE_EDIT_KEY"]);
        Assert.Equal(string.Empty, renewed["OTHER"]);
    }

    [Fact]
    public void A_provider_must_speak_the_version_the_consumer_wants()
    {
        var definition = new PlatformDefinition(2, new PlatformAppInfo("matoffice", "MatOffice", null, "1"), [], [], [], [],
            Provides: [new PlatformCapability("office.edit", 2)]);
        Assert.True(BindingBroker.Provides(definition, new PlatformCapability("office.edit", 1)));
        Assert.True(BindingBroker.Provides(definition, new PlatformCapability("office.edit", 2)));
        Assert.False(BindingBroker.Provides(definition, new PlatformCapability("office.edit", 3)));
        Assert.False(BindingBroker.Provides(definition, new PlatformCapability("files.store", 1)));
        Assert.False(BindingBroker.Provides(null, new PlatformCapability("office.edit", 1)));
    }

    [Fact]
    public void The_variables_of_the_broker_reach_the_web_service_only()
    {
        var yaml = Renderer().Render(Entry("matdrive"), AppOf("matdrive", "web", 4810), ["MAT_OFFICE_EDIT_URL", "MAT_OFFICE_EDIT_KEY", "MAT_API_KEY"]);
        var web = (IDictionary<object, object>)Service(yaml, "web")["environment"];
        Assert.Equal("${MAT_OFFICE_EDIT_URL:-}", web["MAT_OFFICE_EDIT_URL"]);
        Assert.Equal("${MAT_OFFICE_EDIT_KEY:-}", web["MAT_OFFICE_EDIT_KEY"]);

        // A name the catalog entry passes stays as it is written.
        Assert.Equal("${MAT_API_KEY}", web["MAT_API_KEY"]);

        var db = (IDictionary<object, object>)Service(yaml, "db")["environment"];
        Assert.False(db.ContainsKey("MAT_OFFICE_EDIT_URL"));
    }

    [Fact]
    public void Without_variables_of_the_broker_the_compose_file_stays_as_it_was()
        => Assert.Equal(Renderer().Render(Entry("matsplit"), AppOf("matsplit", "matsplit", 4774)), Renderer().Render(Entry("matsplit"), AppOf("matsplit", "matsplit", 4774), []));

    [Fact]
    public void A_list_of_environment_entries_gets_the_variables_as_entries()
    {
        const string yaml = """
            x-matos:
              id: listapp
              name: List app
              ui: { service: app, port: 80 }
            services:
              app:
                image: nginx:alpine
                environment:
                  - TZ=UTC
                  - MAT_INTERNAL_URL=fixed
            """;
        var rendered = Renderer().Render(CatalogParser.Parse(yaml, "listapp.yml"), AppOf("listapp", "app", 80), ["MAT_INTERNAL_URL", "MAT_PUBLIC_URL"]);
        var environment = ((IList<object>)Service(rendered, "app")["environment"]).Select(e => e.ToString()).ToList();
        Assert.Contains("MAT_PUBLIC_URL=${MAT_PUBLIC_URL:-}", environment);
        Assert.Contains("MAT_INTERNAL_URL=fixed", environment);
        Assert.DoesNotContain("MAT_INTERNAL_URL=${MAT_INTERNAL_URL:-}", environment);
    }

    [Fact]
    public void The_env_file_has_the_variables_of_the_broker_but_a_variable_of_the_catalog_wins()
    {
        var files = new ComposeFiles(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), Renderer());
        var spec = files.Build(AppOf("matdrive", "web", 4810), Entry("matdrive"),
            new Dictionary<string, string> { ["DB_PASSWORD"] = "pw", ["TIMEZONE"] = "Europe/Berlin" }, "localhost",
            new Dictionary<string, string> { ["MAT_OFFICE_EDIT_KEY"] = "mo_key", ["TIMEZONE"] = "UTC" });
        Assert.Contains("MAT_OFFICE_EDIT_KEY='mo_key'", spec.Env, StringComparison.Ordinal);
        Assert.Contains("TIMEZONE='Europe/Berlin'", spec.Env, StringComparison.Ordinal);
        Assert.DoesNotContain("TIMEZONE='UTC'", spec.Env, StringComparison.Ordinal);
        Assert.Contains("MAT_OFFICE_EDIT_KEY: \"${MAT_OFFICE_EDIT_KEY:-}\"", spec.Compose, StringComparison.Ordinal);
        Assert.DoesNotContain("TIMEZONE: \"${TIMEZONE:-}\"", spec.Compose, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_of_contract_two_is_read_with_its_contributions()
    {
        const string json = """
            {"contract":2,"app":{"id":"matoffice","name":"MatOffice","version":"1"},"auth":{"apiKey":true},
             "pages":[],"state":[{"key":"documents","label":"Documents","widget":"number"}],"configure":[],"actions":[],
             "provides":[{"capability":"office.edit","version":1,"label":"Edits office files for"}],
             "consumes":[{"capability":"mail.smtp","version":1,"optional":true,"params":{"sender":"office"}}],
             "contributes":{"launchers":[{"id":"docs","label":"Docs","icon":"/img/docs.svg","url":"/home?kind=docs","badge":"documents"}],
                            "widgets":[{"id":"documents","label":"Documents","kind":"stat","size":"small","items":["documents"],"audience":"admin"}],
                            "quickActions":[{"launcher":"docs","label":"New document","icon":"plus","url":"/Documents/New?kind=docs"}]},
             "future":{"something":true}}
            """;
        var definition = JsonSerializer.Deserialize<PlatformDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("office.edit", Assert.Single(definition.Provides!).Capability);
        Assert.Equal("office", definition.Consumes![0].Params!["sender"]);
        Assert.Equal("/home?kind=docs", definition.Contributes!.Launchers![0].Url);
        Assert.Equal("admin", definition.Contributes.Widgets![0].Audience);
        Assert.Equal("docs", definition.Contributes.QuickActions![0].Launcher);
    }

    [Theory]
    [InlineData(null, false, true, true)]
    [InlineData("everyone", false, false, true)]
    [InlineData("admin", false, true, false)]
    [InlineData("admin", true, false, true)]
    [InlineData("person", false, true, true)]
    [InlineData("person", true, false, false)]
    [InlineData("friends", true, true, false)]
    public void Who_sees_a_contribution(string? audience, bool isAdmin, bool launcher, bool sees)
        => Assert.Equal(sees, ShellRules.Sees(audience, isAdmin, launcher));

    [Theory]
    [InlineData("/docs", true)]
    [InlineData("/home?kind=docs", true)]
    [InlineData("docs", false)]
    [InlineData("//evil.example.com", false)]
    [InlineData("/\\evil", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("/redirect?to=https://evil.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_address_of_the_app_is_opened(string? path, bool ok) => Assert.Equal(ok, ShellRules.IsPath(path));

    [Fact]
    public void A_launcher_opens_its_address_in_the_window()
        => Assert.Equal("/Apps/Open?id=7&path=%2Fhome%3Fkind%3Ddocs", ShellRules.WindowOf(AppOf("matoffice", "web", 4820), "/home?kind=docs"));
}
