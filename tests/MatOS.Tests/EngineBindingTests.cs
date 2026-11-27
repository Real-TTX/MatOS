using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Auth;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Tests;

/// <summary>
/// The engines' capabilities through the broker (MatDesign docs/PLATFORM.md "Capabilities", APP-MODEL.md step 5): what an app consumes (its catalog entry, its
/// <c>/api</c>, the implicit <c>web.route</c>) and the variables an earlier MatOS filled, taken over as bindings.
/// </summary>
public class EngineBindingTests
{
    private static readonly PlatformDefinition NoWord = new(2, new PlatformAppInfo("x", "X", null, "1"), [], [], [], []);

    private static InstalledApp App(string slug = "matdrive", string role = AppRoles.App, string? ui = "web", int? hostPort = null)
        => new() { Id = 7, Slug = slug, Name = slug, CatalogId = slug, Role = role, UiService = ui, UiPort = ui is null ? null : 8080, HostPort = hostPort };

    private static CatalogApp Entry(params CatalogConsumes[] consumes) => new() { Id = "matdrive", Name = "MatDrive", Consumes = [.. consumes] };

    private static CatalogConsumes Uses(string capability, params (string Key, string Value)[] parameters)
        => new() { Capability = capability, Params = parameters.ToDictionary(p => p.Key, p => p.Value) };

    [Fact]
    public void An_app_wants_what_its_entry_and_its_api_say_and_the_api_wins()
    {
        var entry = Entry(Uses(Capabilities.IdentityOidc, ("callback", "/auth/callback")), Uses(Capabilities.MailSmtp, ("sender", "drive")));
        var definition = NoWord with
        {
            Consumes =
            [
                new PlatformCapability(Capabilities.MailSmtp, 1, Params: new Dictionary<string, string> { ["sender"] = "files" }),
                new PlatformCapability("office.edit", 1, Optional: true),
                new PlatformCapability(Capabilities.IdentityOidc, 1),
            ],
        };

        var wanted = BindingBroker.Wanted(entry, App(), definition, new SystemConfig()).ToDictionary(c => c.Capability);
        Assert.Equal([Capabilities.IdentityOidc, Capabilities.MailSmtp, "office.edit"], wanted.Keys.Order());
        Assert.Equal("files", wanted[Capabilities.MailSmtp].Params!["sender"]);

        // The app names identity.oidc without parameters: the paths of the entry stay.
        Assert.Equal("/auth/callback", wanted[Capabilities.IdentityOidc].Params!["callback"]);
    }

    [Fact]
    public void An_app_that_does_not_describe_itself_wants_what_its_entry_says()
    {
        var wanted = BindingBroker.Wanted(Entry(Uses(Capabilities.MailSmtp)), App(ui: null), null, new SystemConfig());
        Assert.Equal(Capabilities.MailSmtp, Assert.Single(wanted).Capability);
    }

    [Fact]
    public void Every_app_with_a_web_page_wants_its_route_under_the_domain_or_on_its_port()
    {
        var withDomain = new SystemConfig { BaseDomain = "example.com" };
        var route = Assert.Single(BindingBroker.Wanted(Entry(), App(hostPort: 4810), null, withDomain));
        Assert.Equal(Capabilities.WebRoute, route.Capability);
        Assert.Equal("matdrive.example.com", route.Params!["host"]);
        Assert.False(route.Params.ContainsKey("port"));

        // Without a domain the proxy serves the app on its port; the name of the machine only when the settings know it (else the app is not told localhost).
        var port = Assert.Single(BindingBroker.Wanted(Entry(), App(hostPort: 4810), null, new SystemConfig()));
        Assert.Equal(Capabilities.WebRoute, port.Capability);
        Assert.Equal(new Dictionary<string, string> { ["port"] = "4810" }, port.Params);
        var named = Assert.Single(BindingBroker.Wanted(Entry(), App(hostPort: 4810), null, new SystemConfig { PublicHost = " nas.local " }));
        Assert.Equal(new Dictionary<string, string> { ["port"] = "4810", ["publicHost"] = "nas.local" }, named.Params);

        Assert.Empty(BindingBroker.Wanted(Entry(), App(), null, new SystemConfig()));
        Assert.Empty(BindingBroker.Wanted(Entry(), App(ui: null), null, withDomain));
        Assert.Empty(BindingBroker.Wanted(Entry(), App("matcad", AppRoles.Proxy, hostPort: 4433), null, withDomain));
        Assert.Empty(BindingBroker.Wanted(Entry(), App("matcad", AppRoles.Proxy, hostPort: 4433), null, new SystemConfig()));
    }

    [Fact]
    public async Task A_routed_app_publishes_no_port_and_the_proxy_publishes_the_ports_of_its_port_routes()
    {
        var file = Path.Combine(Path.GetTempPath(), "matos-ports-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new MatOSDbContext(new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + file).Options, new SystemCurrentUserAccessor());
            await db.Database.MigrateAsync();
            var proxy = new InstalledApp { Slug = "matcad", Name = "Matcad", CatalogId = "matcad", Role = AppRoles.Proxy, Lifecycle = AppLifecycle.Installed, ProjectName = "p1", HostPort = 4433 };
            var drive = new InstalledApp { Slug = "matdrive", Name = "MatDrive", CatalogId = "matdrive", Lifecycle = AppLifecycle.Installed, ProjectName = "p2", HostPort = 4810 };
            var todo = new InstalledApp { Slug = "matdo", Name = "MatDo", CatalogId = "matdo", Lifecycle = AppLifecycle.Installed, ProjectName = "p3", HostPort = 6006 };
            var alone = new InstalledApp { Slug = "matpaper", Name = "MatPaper", CatalogId = "matpaper", Lifecycle = AppLifecycle.Installed, ProjectName = "p4", HostPort = 8080 };
            db.InstalledApps.AddRange(proxy, drive, todo, alone);
            await db.SaveChangesAsync();
            db.AppBindings.AddRange(
                new AppBinding { ConsumerAppId = todo.Id, ProviderAppId = proxy.Id, Capability = Capabilities.WebRoute },
                new AppBinding { ConsumerAppId = drive.Id, ProviderAppId = proxy.Id, Capability = Capabilities.WebRoute });
            await db.SaveChangesAsync();

            var config = Config();
            var broker = new BindingBroker(db, null!, null!, null!, new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()), null!, config, null!, null!);
            var withRoutes = new CatalogApp { Id = "matcad", Name = "Matcad", Routes = new CatalogRoutes { Service = "caddy" } };
            var plain = new CatalogApp { Id = "x", Name = "X" };

            Assert.Equal(new ComposePorts(false, []), await broker.PortsAsync(drive, plain, default) with { RoutePorts = [] });
            Assert.False((await broker.PortsAsync(todo, plain, default)).OwnPort);
            // An app the proxy does not route (its route was refused, or there is no proxy) publishes its own port.
            Assert.True((await broker.PortsAsync(alone, plain, default)).OwnPort);
            Assert.Equal([4810, 6006], (await broker.PortsAsync(proxy, withRoutes, default)).RoutePorts);
            Assert.True((await broker.PortsAsync(proxy, withRoutes, default)).OwnPort);

            // Under a domain the routes are host routes: the proxy publishes no port for them.
            await config.UpdateAsync(c => c.BaseDomain = "example.com");
            Assert.Empty((await broker.PortsAsync(proxy, withRoutes, default)).RoutePorts);
            Assert.False((await broker.PortsAsync(drive, plain, default)).OwnPort);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private static SystemConfigService Config()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N")));
        return new SystemConfigService(new JsonConfigStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonConfigStore>.Instance),
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
    }

    [Fact]
    public async Task The_sign_in_and_the_mail_of_an_earlier_MatOS_become_bindings()
    {
        var file = Path.Combine(Path.GetTempPath(), "matos-adopt-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new MatOSDbContext(new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + file).Options, new SystemCurrentUserAccessor());
            await db.Database.MigrateAsync();
            var identity = new InstalledApp { Slug = "matidentity", Name = "MatIdentity", CatalogId = "matidentity", Role = AppRoles.Identity, Lifecycle = AppLifecycle.Installed, ProjectName = "p1" };
            var drive = new InstalledApp { Slug = "matdrive", Name = "MatDrive", CatalogId = "matdrive", Lifecycle = AppLifecycle.Installed, ProjectName = "p2" };
            db.InstalledApps.AddRange(identity, drive);
            await db.SaveChangesAsync();

            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var broker = new BindingBroker(db, null!, null!, null!, protector, null!, null!, null!, null!);
            var stored = new Dictionary<string, string>
            {
                [ManagedVariables.IdentityAuthority] = "https://id.example.com",
                [ManagedVariables.IdentityInternalUrl] = "http://matos-matidentity-matidentity:2300",
                [ManagedVariables.IdentityClientId] = "matdrive",
                // A secret of the old entry was stored protected.
                [ManagedVariables.IdentityClientSecret] = protector.Protect("the-secret"),
                // Mail was filled too, but MatSMTP is not installed: nothing to take over.
                [ManagedVariables.MailHost] = "matos-matsmtp-matsmtp",
            };

            await broker.AdoptAsync(drive, stored, default);
            await broker.AdoptAsync(drive, stored, default);

            var binding = Assert.Single(db.AppBindings);
            Assert.Equal(Capabilities.IdentityOidc, binding.Capability);
            Assert.Equal(identity.Id, binding.ProviderAppId);
            Assert.DoesNotContain("the-secret", binding.VariablesProtected, StringComparison.Ordinal);
            var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(binding.VariablesProtected))!;
            Assert.Equal("the-secret", variables[ManagedVariables.IdentityClientSecret]);
            Assert.Equal("https://id.example.com", variables[ManagedVariables.IdentityAuthority]);
            Assert.Equal("matdrive", variables[ManagedVariables.IdentityClientId]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    /// <summary>The variables of the bindings of identity.oidc, mail.smtp and shell.embed (an engine's own variables, such as its key or issuer, are its own).</summary>
    private static readonly HashSet<string> BindingVariables =
    [
        ManagedVariables.IdentityAuthority, ManagedVariables.IdentityInternalUrl, ManagedVariables.IdentityClientId, ManagedVariables.IdentityClientSecret,
        .. ManagedVariables.Mail, ManagedVariables.FrameAncestors,
    ];

    [Fact]
    public void The_catalog_entries_say_what_they_consume_and_no_engine_variables()
    {
        var catalog = new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogService>.Instance);
        foreach (var entry in catalog.GetAll())
        {
            Assert.Null(entry.Identity);
            Assert.Null(entry.Mail);
            Assert.DoesNotContain(entry.Variables, v => BindingVariables.Contains(v.Key));
        }

        Assert.True(catalog.Get("matsplit")!.Uses(Capabilities.IdentityOidc));
        Assert.True(catalog.Get("matdrive")!.Uses(Capabilities.MailSmtp));
    }
}
