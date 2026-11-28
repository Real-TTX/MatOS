using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

/// <summary>
/// The well-known apps of other makers in the catalog (Nextcloud, Immich, Jellyfin, Vaultwarden, Paperless-ngx, Uptime Kuma, Home Assistant): each parses, renders a
/// compose file that compose reads as written (the start scripts and the hook keep their <c>$$</c>), and those that sign in through MatIdentity or send mail through
/// MatSMTP ask for it with the parameters of their own paths.
/// </summary>
public class ThirdPartyAppTests
{
    public static readonly TheoryData<string> Apps = ["nextcloud", "immich", "jellyfin", "vaultwarden", "paperless-ngx", "uptime-kuma", "home-assistant"];

    private static readonly ComposeRenderer Renderer = new(Options.Create(new MatOSOptions()));

    private static CatalogApp Entry(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return CatalogParser.Parse(reader.ReadToEnd(), id + ".yml");
    }

    private static InstalledApp AppOf(CatalogApp entry)
        => new() { Id = 7, Slug = entry.Id, Name = entry.Name, ProjectName = "matos-" + entry.Id, UiService = entry.Ui!.Service, UiPort = entry.Ui.Port, HostPort = 18000, CatalogId = entry.Id };

    private static Dictionary<string, object> Parse(string yaml) => new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

    private static IDictionary<object, object> Map(object node) => (IDictionary<object, object>)node;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatOS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatOS.slnx was not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_entry_is_a_third_party_app_with_its_icon_a_tagged_image_and_named_volumes(string id)
    {
        var entry = Entry(id);
        Assert.Equal(AppStates.ThirdParty, entry.State);
        Assert.False(string.IsNullOrWhiteSpace(entry.StateNote));
        Assert.Equal(AppRoles.App, entry.Role);
        Assert.NotNull(entry.Ui);
        Assert.True(File.Exists(Path.Combine(Root(), "src", "MatOS.Web", "wwwroot", "img", "apps", entry.Icon!)), $"{id}: icon {entry.Icon}");

        // No port of its own (MatOS publishes the UI, Matcad routes it), no bind mount (it would resolve inside MatOS), every image with a tag.
        Assert.Empty(entry.FixedPorts);
        var compose = Parse(entry.ComposeYaml);
        foreach (var (name, node) in Map(compose["services"]))
        {
            var service = Map(node);
            var image = (string)service["image"];
            Assert.True(image.LastIndexOf(':') > image.LastIndexOf('/'), $"{id}/{name}: the image {image} names a tag");
            foreach (var volume in service.TryGetValue("volumes", out var v) ? ((IEnumerable<object>)v).Select(x => x.ToString()!) : [])
            {
                Assert.False(volume.StartsWith('.') || volume.StartsWith('/'), $"{id}/{name}: {volume} is a bind mount");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_rendered_compose_file_keeps_the_entry_and_adds_what_MatOS_knows(string id)
    {
        var entry = Entry(id);
        var app = AppOf(entry);
        var original = Parse(entry.ComposeYaml);
        var rendered = Parse(Renderer.Render(entry, app, ["MATIDENTITY_CLIENT_ID", "MAT_PUBLIC_URL"], new ComposePorts(false, [])));

        foreach (var (key, node) in Map(rendered["services"]))
        {
            var name = (string)key;
            var service = Map(node);
            var before = Map(Map(original["services"])[name]);
            Assert.Equal(Renderer.ContainerName(entry.Id, name), service["container_name"]);

            // The start scripts (entrypoint) and the volumes are the entry's, character for character.
            foreach (var field in new[] { "entrypoint", "volumes", "image", "configs" }.Where(before.ContainsKey))
            {
                Assert.Equal(Serialize(before[field]), Serialize(service[field]));
            }

            // Routed through Matcad: no port of its own.
            Assert.False(service.ContainsKey("ports"), $"{id}/{name} publishes a port");
        }

        var ui = Map(Map(rendered["services"])[entry.Ui!.Service]);
        Assert.Contains(new MatOSOptions().NetworkName, ((IEnumerable<object>)ui["networks"]).Select(n => n.ToString()));
        if (original.TryGetValue("configs", out var configs))
        {
            Assert.Equal(Serialize(configs), Serialize(rendered["configs"]));
        }
    }

    [Fact]
    public void The_start_scripts_leave_the_shell_its_dollars()
    {
        // $$ is compose's way of writing a $ for the shell; a single $ would be filled (with nothing) by compose before the script runs.
        foreach (var id in new[] { "vaultwarden", "paperless-ngx", "nextcloud" })
        {
            var text = Renderer.Render(Entry(id), AppOf(Entry(id)));
            Assert.Contains("$$MATIDENTITY_CLIENT_ID", text, StringComparison.Ordinal);
            Assert.Contains("$$MATSMTP_HOST", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"$MAT", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("vaultwarden", "/identity/connect/oidc-signin", "vaultwarden")]
    [InlineData("paperless-ngx", "/accounts/oidc/matidentity/login/callback/", "paperless")]
    [InlineData("nextcloud", "/apps/user_oidc/code", "nextcloud")]
    public void The_apps_with_a_sign_in_of_their_own_use_MatIdentity_and_MatSMTP(string id, string callback, string sender)
    {
        var entry = Entry(id);
        var identity = Assert.Single(entry.Consumes, c => c.Capability == Capabilities.IdentityOidc);
        Assert.Equal(callback, identity.Params["callback"]);

        // None of them takes OpenID Connect's back-channel logout: MatIdentity is told so (an empty path).
        Assert.Equal(string.Empty, identity.Params["backchannel"]);
        Assert.Equal(sender, Assert.Single(entry.Consumes, c => c.Capability == Capabilities.MailSmtp).Params["sender"]);

        // What the broker asks MatIdentity for (no /api of the app: the catalog's word).
        var wanted = BindingBroker.Wanted(entry, AppOf(entry), definition: null, new SystemConfig());
        Assert.Equal(callback, Assert.Single(wanted, w => w.Capability == Capabilities.IdentityOidc).Params!["callback"]);
    }

    [Theory]
    [InlineData("immich")]
    [InlineData("jellyfin")]
    [InlineData("uptime-kuma")]
    [InlineData("home-assistant")]
    public void The_others_run_as_they_are(string id) => Assert.Empty(Entry(id).Consumes);

    [Theory]
    [InlineData("vaultwarden", true)]
    [InlineData("nextcloud", true)]
    [InlineData("paperless-ngx", true)]
    [InlineData("jellyfin", true)]
    [InlineData("immich", false)]
    [InlineData("portainer", false)]
    public void An_app_of_another_maker_learns_its_public_address_when_its_entry_names_it(string id, bool names)
        => Assert.Equal(names, BindingBroker.NamesPublicUrl(Entry(id)));

    private static string Serialize(object? node) => new SerializerBuilder().Build().Serialize(node);
}
