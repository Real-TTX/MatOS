using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

public class EngineTests
{
    private static SystemConfigService CreateConfig()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N")));
        var store = new JsonConfigStore(paths, NullLogger<JsonConfigStore>.Instance);
        return new SystemConfigService(store, new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
    }

    private static CatalogApp Entry(string id)
        => new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance).Get(id)!;

    // ------------------------------------------------------------------ the engines in the catalog

    [Theory]
    [InlineData("matdock", "MATDOCK_API_KEY", "MatDock__Api__Key")]
    [InlineData("matcad", "MATCAD_API_KEY", "Matcad__ApiKey")]
    [InlineData("matddns", "MATDDNS_API_KEY", "MATDDNS_API_KEY")]
    public void Engines_AreCore_AndHandTheirApiKeyToTheContainer(string id, string variable, string environment)
    {
        var entry = Entry(id);

        Assert.True(entry.Core);
        Assert.Equal(id, entry.Provision);
        var key = Assert.Single(entry.Variables, v => v.Key == variable);
        Assert.True(key.Generate);
        Assert.True(key.Hidden);
        Assert.Contains($"{environment}: ${{{variable}}}", entry.ComposeYaml);
    }

    [Fact]
    public void EngineIds_AreTheCatalogEntriesThatAreCore()
    {
        var core = new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance)
            .GetAll().Where(a => a.Core).Select(a => a.Id).Order().ToList();

        Assert.Equal(EngineIds.All.Order().ToList(), core);
        Assert.Equal(EngineIds.MatDock, EngineIds.All[0]); // it deploys the others
        Assert.True(EngineIds.IsEngine("matcad"));
        Assert.False(EngineIds.IsEngine("matmail"));
        Assert.False(EngineIds.IsEngine(null));
    }

    [Fact]
    public void Matcad_GetsItsAdminPasswordGenerated_AndShowsIt()
    {
        var entry = Entry("matcad");

        Assert.Contains(entry.Variables, v => v.Key == "MATCAD_ADMIN_PASSWORD" && v.Generate && v.Type == "secret");
        Assert.Contains(entry.Credentials, c => c.Value == "${MATCAD_ADMIN_PASSWORD}" && c.Secret);
    }

    // ------------------------------------------------------------------ DNS account form

    [Fact]
    public void DnsForm_CredentialsOf_KeepsOnlyTheChosenProvidersFields()
    {
        var posted = new Dictionary<string, string?>
        {
            ["cloudflare:apiToken"] = "cf-token",
            ["hetzner:apiToken"] = "hz-token",
            ["netcup:apiKey"] = "nc-key",
        };

        var credentials = DnsForm.CredentialsOf("hetzner", posted);

        Assert.Equal(["apiToken"], credentials.Keys);
        Assert.Equal("hz-token", credentials["apiToken"]);
        Assert.Empty(DnsForm.CredentialsOf("", posted));
        Assert.Empty(DnsForm.CredentialsOf("unknown", posted));
    }

    [Fact]
    public void DnsForm_Missing_NamesTheFieldsWithoutAnyValue()
    {
        var posted = new Dictionary<string, string?> { ["customerNumber"] = "123", ["apiKey"] = " ", ["apiPassword"] = null };
        var stored = new Dictionary<string, string> { ["apiPassword"] = "saved" };

        var missing = DnsForm.Missing("netcup", posted, stored);

        Assert.Equal(["API key"], missing);
        Assert.Empty(DnsForm.Missing("", posted, stored));
    }

    [Fact]
    public async Task SetDns_KeepsStoredSecrets_WhenAFieldIsLeftEmpty()
    {
        var config = CreateConfig();
        await config.SetDnsAsync("netcup", "example.com", new Dictionary<string, string?> { ["customerNumber"] = "1", ["apiKey"] = "key", ["apiPassword"] = "pw" });

        await config.SetDnsAsync("netcup", "example.com", new Dictionary<string, string?> { ["customerNumber"] = "2", ["apiKey"] = "", ["apiPassword"] = null });

        var stored = await config.GetDnsCredentialsAsync();
        Assert.Equal("2", stored["customerNumber"]);
        Assert.Equal("key", stored["apiKey"]);
        Assert.Equal("pw", stored["apiPassword"]);
        var saved = await config.GetAsync();
        Assert.Equal("netcup", saved.Dns.Provider);
        Assert.Equal("example.com", saved.Dns.Zone);
        Assert.True(saved.Dns.IsConfigured);
    }

    [Fact]
    public async Task SetDns_StartsFromNothing_WhenTheProviderChanges()
    {
        var config = CreateConfig();
        await config.SetDnsAsync("cloudflare", null, new Dictionary<string, string?> { ["apiToken"] = "cloudflare-token" });

        // Both providers call the field "apiToken", but it is another token: the old one must not carry over.
        await config.SetDnsAsync("hetzner", null, new Dictionary<string, string?> { ["apiToken"] = null });

        Assert.Empty(await config.GetDnsCredentialsAsync());
        Assert.Equal("hetzner", (await config.GetAsync()).Dns.Provider);
    }

    [Fact]
    public async Task SetDns_None_RemovesTheCredentials_AndTheSecretIsNotInTheFile()
    {
        var config = CreateConfig();
        await config.SetDnsAsync("cloudflare", null, new Dictionary<string, string?> { ["apiToken"] = "super-secret-token" });
        var protectedValue = (await config.GetAsync()).ProtectedSecrets["dns"];

        Assert.DoesNotContain("super-secret-token", protectedValue);

        await config.SetDnsAsync("none", null, new Dictionary<string, string?>());

        Assert.Empty(await config.GetDnsCredentialsAsync());
        Assert.False((await config.GetAsync()).Dns.IsConfigured);
        Assert.False((await config.GetAsync()).ProtectedSecrets.ContainsKey("dns"));
    }

    [Fact]
    public void DnsProviders_MapTheirCredentialsToTheNamesOfMatcad()
    {
        var netcup = DnsProviders.Find("NETCUP")!;

        var matcad = DnsProviders.ToMatcad(netcup, new Dictionary<string, string> { ["customerNumber"] = "1", ["apiKey"] = "k", ["apiPassword"] = "p" });

        Assert.Equal("1", matcad["customer_number"]);
        Assert.Equal("k", matcad["api_key"]);
        Assert.Equal("p", matcad["api_password"]);
    }

    // ------------------------------------------------------------------ backups from more than one place

    [Theory]
    [InlineData("local:12", "local", "12")]
    [InlineData("matdock:abc-1", "matdock", "abc-1")]
    [InlineData("matdock:nightly: all", "matdock", "nightly: all")]
    [InlineData("7", "local", "7")]
    public void BackupHub_SplitId_TakesTheEngineBeforeTheFirstColon(string id, string engine, string raw)
        => Assert.Equal((engine, raw), BackupHub.SplitId(id));
}
