using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using Microsoft.AspNetCore.DataProtection;
using MatOS.Core.Security;

namespace MatOS.Tests;

public class VariablesTests
{
    private static CatalogApp App() => new()
    {
        Id = "demo",
        Name = "Demo",
        Variables =
        [
            new CatalogVariable { Key = "ADMIN_PASSWORD", Label = "Admin password", Type = "secret", Generate = true },
            new CatalogVariable { Key = "HOST", Label = "Host", Default = "localhost" },
            new CatalogVariable { Key = "PORT", Label = "Port", Type = "number", Default = "25" },
            new CatalogVariable { Key = "TOKEN", Label = "Token", Required = true },
        ],
        Credentials =
        [
            new CatalogCredential { Label = "Password", Value = "${ADMIN_PASSWORD}", Secret = true },
            new CatalogCredential { Label = "Host", Value = "${HOST}" },
            new CatalogCredential { Label = "Empty", Value = "${NOPE}" },
        ],
    };

    private static ISecretProtector Protector()
        => new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public void Resolve_UsesTypedValue_ThenGenerated_ThenDefault()
    {
        var errors = new List<string>();

        var values = AppVariables.Resolve(App(), new Dictionary<string, string?> { ["HOST"] = " nas.local ", ["TOKEN"] = "t" }, null, errors);

        Assert.Empty(errors);
        Assert.Equal("nas.local", values["HOST"]);
        Assert.Equal("25", values["PORT"]);
        Assert.Equal(24, values["ADMIN_PASSWORD"].Length);
        Assert.All(values["ADMIN_PASSWORD"], c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void Resolve_KeepsKnownValues_OnUpdate()
    {
        var errors = new List<string>();
        var existing = new Dictionary<string, string> { ["ADMIN_PASSWORD"] = "old-secret", ["TOKEN"] = "t" };

        var values = AppVariables.Resolve(App(), new Dictionary<string, string?>(), existing, errors);

        Assert.Empty(errors);
        Assert.Equal("old-secret", values["ADMIN_PASSWORD"]);
    }

    [Fact]
    public void Resolve_ReportsMissingRequiredNumbersAndQuotes()
    {
        var errors = new List<string>();

        AppVariables.Resolve(App(), new Dictionary<string, string?> { ["PORT"] = "abc", ["HOST"] = "it's" }, null, errors);

        Assert.Contains(errors, e => e.Contains("Token is required"));
        Assert.Contains(errors, e => e.Contains("Port must be a number"));
        Assert.Contains(errors, e => e.Contains("Host must not contain"));
    }

    [Fact]
    public void GenerateSecret_IsRandom()
        => Assert.NotEqual(AppVariables.GenerateSecret(), AppVariables.GenerateSecret());

    [Fact]
    public void Storage_ProtectsSecrets_AndRoundTrips()
    {
        var protector = Protector();
        var values = new Dictionary<string, string> { ["ADMIN_PASSWORD"] = "s3cret", ["HOST"] = "nas.local" };

        var json = AppVariables.Serialize(App(), values, protector);
        var back = AppVariables.Deserialize(App(), json, protector);

        Assert.DoesNotContain("s3cret", json);
        Assert.Contains("nas.local", json);
        Assert.Equal("s3cret", back["ADMIN_PASSWORD"]);
        Assert.Equal("nas.local", back["HOST"]);
    }

    [Fact]
    public void Credentials_ResolveVariables_DropEmptyLines_AndProtectSecrets()
    {
        var protector = Protector();
        var variables = new Dictionary<string, string> { ["ADMIN_PASSWORD"] = "s3cret", ["HOST"] = "nas.local" };

        var lines = AppCredentials.Build(App(), variables);
        var json = AppCredentials.Serialize(lines, protector);
        var back = AppCredentials.Deserialize(json, protector);

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain("s3cret", json);
        Assert.Equal("s3cret", back.Single(l => l.Label == "Password").Value);
        Assert.Equal("nas.local", back.Single(l => l.Label == "Host").Value);
    }

    [Theory]
    [InlineData("${A}", "1")]
    [InlineData("${B:-fallback}", "fallback")]
    [InlineData("${A:-x}-${B-y}", "1-y")]
    [InlineData("plain", "plain")]
    public void EnvSubstitution_FollowsComposeRules(string text, string expected)
        => Assert.Equal(expected, EnvSubstitution.Apply(text, new Dictionary<string, string> { ["A"] = "1" }));

    [Theory]
    [InlineData("matcad", "matcad")]
    [InlineData("  Mat Mail 2 ", "mat-mail-2")]
    [InlineData("MatMail!!", "matmail")]
    [InlineData("---", null)]
    [InlineData("", null)]
    public void Slugify_NormalizesNames(string input, string? expected)
        => Assert.Equal(expected, AppService.Slugify(input));
}
