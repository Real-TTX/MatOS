using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>The platform contract and the single sign-on in the catalog: the variables MatOS fills itself, and the entries of MatIdentity and MatSplit.</summary>
public class PlatformTests
{
    private const string Minimal = """
        x-matos:
          id: demo
          name: Demo
          ui:
            service: web
            port: 8080
          platform: true
          identity: {}
        services:
          web:
            image: nginx
        """;

    private static CatalogService CreateCatalog()
        => new(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    [Fact]
    public void An_app_of_the_platform_gets_its_key_and_the_old_identity_key_is_the_capability()
    {
        var app = CatalogParser.Parse(Minimal, "test");

        var key = Assert.Single(app.Variables, v => v.Key == ManagedVariables.ApiKey);
        Assert.True(key.Hidden && key.Generate && key.IsSecret);

        // identity: {} is read as identity.oidc with its paths as parameters; the variables are the broker's, not the entry's.
        var identity = Assert.Single(app.Consumes);
        Assert.Equal(Capabilities.IdentityOidc, identity.Capability);
        Assert.Equal("/signin-oidc", identity.Params["callback"]);
        Assert.Equal("/signout-callback-oidc", identity.Params["signedOut"]);
        Assert.Equal("/signout-backchannel", identity.Params["backchannel"]);
        Assert.DoesNotContain(app.Variables, v => v.Key.StartsWith("MATIDENTITY_", StringComparison.Ordinal));
    }

    [Fact]
    public void An_app_without_the_platform_gets_none_of_them()
    {
        var app = CatalogParser.Parse(Minimal.Replace("  platform: true\n", string.Empty).Replace("  identity: {}\n", string.Empty), "test");
        Assert.DoesNotContain(app.Variables, v => v.Key.StartsWith("MAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Platform_and_identity_need_a_web_service()
        => Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("  ui:\n    service: web\n    port: 8080\n", string.Empty), "test"));

    [Fact]
    public void MatIdentity_is_in_the_catalog_as_the_single_sign_on()
    {
        var identity = CreateCatalog().Get(MatIdentityClient.CatalogIdValue);
        Assert.NotNull(identity);
        Assert.Equal(AppRoles.Identity, identity.Role);
        Assert.Equal("matidentity", identity.Provision);
        Assert.True(identity.Platform);
        Assert.Contains(identity.Variables, v => v.Key == ManagedVariables.IdentityIssuer);
        Assert.Contains("MatIdentity__Issuer: ${MATIDENTITY_ISSUER:-}", identity.ComposeYaml);
        Assert.Contains("MAT_API_KEY: ${MAT_API_KEY}", identity.ComposeYaml);
    }

    [Theory]
    [InlineData("matsplit")]
    [InlineData("mattrakr")]
    [InlineData("matplay")]
    [InlineData("matbu")]
    [InlineData("matmail")]
    [InlineData("matpaper")]
    [InlineData("matfile")]
    [InlineData("matdo")]
    [InlineData("matddns")]
    [InlineData("matcad")]
    [InlineData("matdock")]
    [InlineData("matsmtp")]
    public void The_app_speaks_the_platform_and_signs_in_through_MatIdentity(string id)
    {
        var app = CreateCatalog().Get(id);
        Assert.NotNull(app);
        Assert.True(app.Platform);
        Assert.True(app.Uses(Capabilities.IdentityOidc));
        Assert.Null(app.Identity);
        Assert.Contains($"{ManagedVariables.ApiKey}: ${{{ManagedVariables.ApiKey}", app.ComposeYaml);
    }
}
