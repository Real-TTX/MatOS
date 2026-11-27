using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

public class CatalogTests
{
    private static CatalogService CreateCatalog(string? dataPath = null)
        => new(new AppPaths(dataPath ?? Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    [Fact]
    public void BuiltInCatalog_LoadsEveryEntry()
    {
        var apps = CreateCatalog().GetAll();

        // One file per entry: a broken file is skipped (and logged), so the count would drop.
        var files = typeof(CatalogService).Assembly.GetManifestResourceNames().Count(n => n.StartsWith("catalog/"));
        Assert.Equal(files, apps.Count);
        Assert.NotEmpty(apps);
    }

    [Fact]
    public void BuiltInCatalog_HasTheWizardRoles()
    {
        var catalog = CreateCatalog();

        Assert.Contains(catalog.GetByRole(AppRoles.Proxy), a => a.Id == "matcad");
        Assert.Contains(catalog.GetByRole(AppRoles.Ddns), a => a.Id == "matddns");
        Assert.Contains(catalog.GetByRole(AppRoles.Docker), a => a.Id == "matdock");
        Assert.True(catalog.GetByRole(AppRoles.Proxy)[0].Recommended);
    }

    [Fact]
    public void BuiltInCatalog_EntriesAreConsistent()
    {
        foreach (var app in CreateCatalog().GetAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Tagline), $"{app.Id}: tagline");
            Assert.True(app.Ui is null || app.ServiceNames.Contains(app.Ui.Service), $"{app.Id}: ui service");

            // The UI port is published by MatOS itself; the compose file must not claim a host port for it.
            if (app.Ui is { } ui)
            {
                Assert.DoesNotContain(app.FixedPorts, p => p.HostPort == ui.HostPort && ui.HostPort is not null && p.Raw.EndsWith($":{ui.Port}", StringComparison.Ordinal));
            }

            foreach (var variable in app.Variables)
            {
                Assert.False(string.IsNullOrWhiteSpace(variable.Key), $"{app.Id}: variable key");
                Assert.True(app.ComposeYaml.Contains("${" + variable.Key, StringComparison.Ordinal), $"{app.Id}: variable {variable.Key} is not used in the compose file");
            }

            // Every ${KEY} of the compose file must be answered by a variable (or carry a default), or compose would run with an empty value.
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(app.ComposeYaml, @"\$\{(?<key>[A-Z_][A-Z0-9_]*)(?<default>:?-[^}]*)?\}"))
            {
                var key = match.Groups["key"].Value;
                var known = app.Variables.Any(v => v.Key == key) || key is "MV_SLUG" or "MV_PROJECT" or "MV_PUBLIC_HOST" or "TZ";
                Assert.True(known || match.Groups["default"].Success, $"{app.Id}: ${{{key}}} has no variable and no default");
            }
        }
    }

    [Fact]
    public void Catalog_ReadsUserFilesFromTheDataFolder_AndTheyReplaceBuiltIns()
    {
        var data = Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(data);
        paths.EnsureCreated();
        File.WriteAllText(Path.Combine(paths.CatalogPath, "whoami.yml"), """
            x-matos:
              id: whoami
              name: Whoami
              tagline: Test
              ui: { service: web, port: 80 }
            services:
              web:
                image: traefik/whoami
            """);
        File.WriteAllText(Path.Combine(paths.CatalogPath, "matcad.yml"), """
            x-matos:
              id: matcad
              name: Custom Matcad
              tagline: Replaced
              role: proxy
            services:
              caddy:
                image: caddy
            """);
        File.WriteAllText(Path.Combine(paths.CatalogPath, "broken.yml"), "services: [");

        var catalog = new CatalogService(paths, NullLogger<CatalogService>.Instance);

        Assert.NotNull(catalog.Get("whoami"));
        Assert.False(catalog.Get("whoami")!.BuiltIn);
        Assert.Equal("Custom Matcad", catalog.Get("matcad")!.Name);
        Assert.Null(catalog.Get("broken"));
    }

    [Theory]
    [InlineData("services:\n  a:\n    image: x\n", "x-matos")]
    [InlineData("x-matos:\n  id: Bad Id\n  name: X\nservices:\n  a:\n    image: x\n", "'id'")]
    [InlineData("x-matos:\n  id: ok\n  name: X\n  ui: { service: nope, port: 80 }\nservices:\n  a:\n    image: x\n", "ui.service")]
    [InlineData("x-matos:\n  id: ok\n  name: X\n", "services")]
    public void Parser_RejectsInvalidEntries(string yaml, string expectedMessagePart)
    {
        var ex = Assert.Throws<FormatException>(() => CatalogParser.Parse(yaml, "test.yml"));
        Assert.Contains(expectedMessagePart, ex.Message);
    }

    [Theory]
    [InlineData("80:80", 80, "tcp")]
    [InlineData("443:443/udp", 443, "udp")]
    [InlineData("127.0.0.1:9293:9293", 9293, "tcp")]
    [InlineData("${SMTP_PORT:-25}:25", 25, "tcp")]
    public void ParsePort_ReadsTheHostSide(string text, int host, string protocol)
    {
        var port = CatalogParser.ParsePort(text);

        Assert.NotNull(port);
        Assert.Equal(host, port!.HostPort);
        Assert.Equal(protocol, port.Protocol);
    }

    [Fact]
    public void ParsePort_IgnoresAnEphemeralHostPort() => Assert.Null(CatalogParser.ParsePort("8080"));

    [Fact]
    public void Apps_whose_start_page_does_not_ask_who_is_there_say_where_their_sign_in_starts()
    {
        var catalog = CreateCatalog();

        // With MatIdentity MatOS opens them at their sign-in (AppService.StartPathAsync), so nobody clicks "Sign in" first.
        Assert.All(new[] { "matdo", "matdrive", "matoffice", "matboard", "matshape", "matcloud" }, id => Assert.Equal("/Account/Login", catalog.Get(id)!.Ui!.SignIn));
        // MatFile opens its sign-in by itself (its start page sends a stranger there): it needs none.
        Assert.Null(catalog.Get("matfile")!.Ui!.SignIn);
        Assert.All(new[] { "matphoto", "matcontacthub", "matdown", "matregistry", "matgame", "matsplit", "mattrakr", "matmail", "matpaper" }, id => Assert.Equal("/Account/Login", catalog.Get(id)!.Ui!.SignIn));
    }

    [Fact]
    public void MatIdentity_sends_its_mail_from_a_neutral_sender()
    {
        // The sign-in of MatOS hides MatIdentity; the From of its mails (a new password, an invitation) does too.
        var mail = Assert.Single(CreateCatalog().Get("matidentity")!.Consumes, c => c.Capability == Capabilities.MailSmtp);
        Assert.Equal("account", mail.Params["sender"]);
    }

    [Fact]
    public void MatAi_needs_nothing_set_up_before_it_opens()
    {
        var matai = CreateCatalog().Get("matai")!;

        // Its administrator is seeded from a generated password nobody is asked for (no first-run page), and it signs in through MatIdentity.
        Assert.Contains(matai.Variables, v => v.Key == "MATAI_ADMIN_PASSWORD" && v.Generate && v.Hidden);
        Assert.Contains(matai.Consumes, c => c.Capability == Capabilities.IdentityOidc);
        Assert.Equal(2301, matai.Ui!.Port);
    }

    [Theory]
    [InlineData("matbu", "MATBU_ADMIN_PASSWORD")]
    [InlineData("matsplit", "MATSPLIT_ADMIN_PASSWORD")]
    [InlineData("matplay", "MATPLAY_ADMIN_PASSWORD")]
    [InlineData("matdo", "MATDO_ADMIN_PASSWORD")]
    [InlineData("matdrive", "MATDRIVE_ADMIN_PASSWORD")]
    [InlineData("matoffice", "MATOFFICE_ADMIN_PASSWORD")]
    [InlineData("matboard", "MATBOARD_ADMIN_PASSWORD")]
    [InlineData("matshape", "MATSHAPE_ADMIN_PASSWORD")]
    [InlineData("matcloud", "MATCLOUD_ADMIN_PASSWORD")]
    [InlineData("matcmscloud", "MATCMSCLOUD_ADMIN_PASSWORD")]
    public void The_administrator_of_an_app_is_seeded_from_a_generated_password_nobody_is_asked_for(string id, string variable)
    {
        var app = CreateCatalog().Get(id)!;

        // No fixed admin/admin and no "the first one who registers": the password is generated, hidden at the install and shown with the credentials.
        Assert.Contains(app.Variables, v => v.Key == variable && v.Generate && v.Hidden);
        Assert.Contains(app.Credentials, c => c.Secret && c.Value == "${" + variable + "}");
        Assert.DoesNotContain(app.Credentials, c => c.Value == "admin" && c.Label == "Password");
    }

    [Fact]
    public void MatPaper_needs_nothing_set_up_before_it_opens()
    {
        var matpaper = CreateCatalog().Get("matpaper")!;

        // Its administrator is seeded from a generated password nobody is asked for (no setup page); people sign in through MatIdentity.
        Assert.Contains(matpaper.Variables, v => v.Key == "MATPAPER_ADMIN_PASSWORD" && v.Generate && v.Hidden);
        Assert.Contains(matpaper.Consumes, c => c.Capability == Capabilities.IdentityOidc);
    }

    [Fact]
    public void MatMail_PublishesItsMailPortsAsFixedPorts()
    {
        var matmail = CreateCatalog().Get("matmail")!;

        Assert.Equal(new[] { 25, 587, 465, 143, 993 }, matmail.FixedPorts.Select(p => p.HostPort).ToArray());
    }

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatOS.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("MatOS.slnx was not found"), "src", "MatOS.Web", "wwwroot");
    }

    [Fact]
    public void Every_icon_and_picture_of_the_store_is_there()
    {
        var web = WebRoot();
        foreach (var app in CreateCatalog().GetAll())
        {
            if (app.Icon is { } icon && !icon.StartsWith("https://", StringComparison.Ordinal))
            {
                Assert.True(File.Exists(Path.Combine(web, "img", "apps", icon)), $"{app.Id}: the icon {icon} is missing");
            }

            foreach (var shot in app.Screenshots)
            {
                Assert.False(shot.IsUrl, $"{app.Id}: a built-in entry ships its pictures");
                Assert.True(File.Exists(Path.Combine(web, "img", "store", shot.Src)), $"{app.Id}: the picture {shot.Src} is missing");
                Assert.False(string.IsNullOrWhiteSpace(shot.Caption));
            }
        }
    }

    [Fact]
    public void Every_Mat_app_shows_pictures_in_the_store()
        => Assert.All(CreateCatalog().GetAll().Where(a => a.Role == AppRoles.App && a.Id.StartsWith("mat", StringComparison.Ordinal) && a.Id != "matgame"), // MatGame's repository has no pictures yet
            app => Assert.InRange(app.Screenshots.Count, 1, 6));

    [Theory]
    [InlineData("../secret.png", "Caption")]
    [InlineData("/etc/passwd.png", "Caption")]
    [InlineData("http://example.com/a.png", "Caption")]
    [InlineData("app/home.exe", "Caption")]
    [InlineData("app/home.webp", "")]
    public void A_picture_is_a_file_of_the_store_or_an_https_address_with_a_caption(string src, string caption)
    {
        var yaml = $"""
            x-matos:
              id: mine
              name: Mine
              screenshots:
                - src: "{src}"
                  caption: "{caption}"
            services:
              web:
                image: nginx
            """;

        Assert.Throws<FormatException>(() => CatalogParser.Parse(yaml, "mine.yml"));
    }

    [Fact]
    public void A_picture_may_be_an_https_address()
    {
        var app = CatalogParser.Parse("""
            x-matos:
              id: mine
              name: Mine
              screenshots:
                - src: https://example.com/a.png
                  caption: Home
                - src: mine/b.webp
                  caption: More
            services:
              web:
                image: nginx
            """, "mine.yml");

        Assert.True(app.Screenshots[0].IsUrl);
        Assert.Equal("mine/b.webp", app.Screenshots[1].Src);
    }

    [Fact]
    public void MatCMS_Cloud_signs_in_through_MatIdentity_mails_through_MatSMTP_and_links_the_MatCMS_sites()
    {
        var catalog = CreateCatalog();
        var cloud = catalog.Get("matcmscloud")!;

        // An app of the contract: MAT_API_KEY, so the broker reads its /api (it provides cms.cloud there) and binds the sites to it.
        Assert.True(cloud.Platform);
        Assert.Contains(cloud.Variables, v => v.Key == "MAT_API_KEY" && v.Generate && v.Hidden);
        Assert.Contains(cloud.Consumes, c => c.Capability == Capabilities.IdentityOidc);
        Assert.Equal("cloud", Assert.Single(cloud.Consumes, c => c.Capability == Capabilities.MailSmtp).Params["sender"]);
        Assert.Equal("/login", cloud.Ui!.SignIn);
        Assert.Equal(("web", 8080), (cloud.Ui.Service, cloud.Ui.Port));

        // One volume and NO Docker socket: MatOS updates the sites it installed, the cloud only tells about an update (the entry says so).
        Assert.Contains("matcmscloud-data:/app/appdata", cloud.ComposeYaml);
        Assert.DoesNotContain("docker.sock:/var/run/docker.sock", cloud.ComposeYaml.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Select(l => l.Trim()).Where(l => l.StartsWith("- ")));
        Assert.Contains(cloud.Notes, n => n.Contains("No Docker socket"));

        // MatCMS has no /api of the contract: its entry says it consumes cms.cloud, so a site joins a cloud installed beside it.
        Assert.Contains(catalog.Get("matcms")!.Consumes, c => c.Capability == "cms.cloud" && c.Version == 1);
    }

    [Fact]
    public void No_service_has_an_empty_environment()
    {
        // "environment:" with nothing under it is null, which docker compose refuses ("must be a mapping"): MatCMS could not be installed with it.
        foreach (var app in CreateCatalog().GetAll())
        {
            var lines = app.ComposeYaml.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "environment:") continue;
                var indent = lines[i].Length - lines[i].TrimStart().Length;
                var next = lines.Skip(i + 1).FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')) ?? "";
                Assert.True(next.Length - next.TrimStart().Length > indent, $"{app.Id}: an empty environment: (line {i + 1})");
            }
        }
    }
}
