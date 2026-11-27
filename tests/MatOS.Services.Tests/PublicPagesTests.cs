using System.Net;
using System.Text.RegularExpressions;

namespace MatOS.Services.Tests;

/// <summary>The public pages (/, the DynDNS pages) in the stack of the admin area: MatUi, a strict policy, no cookie, the same form protections.</summary>
public partial class PublicPagesTests
{
    private const string ZoneHost = RunningService.Zone;

    [Theory]
    [InlineData(null, "/", "MatOS Services", true)]
    [InlineData(ZoneHost, "/", "Your own name on the internet, free", false)]
    [InlineData(null, "/dyndns", "Your own name on the internet, free", false)]
    [InlineData(ZoneHost, "/dyndns/rules", "The rules", false)]
    [InlineData(ZoneHost, "/dyndns/manage", "Manage your name", false)]
    public async Task A_public_page_is_built_from_the_toolkit_with_a_strict_policy_and_sets_no_cookie(string? host, string path, string heading, bool noIndex)
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("style-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"), "a public page sets no cookie");

        Assert.Contains("/matui/matui.css", html);
        Assert.Contains("/css/services.css", html);
        Assert.Contains("class=\"mat-page mat-page--narrow\"", html);
        Assert.Single(Regex.Matches(html, "<h1>"));
        Assert.Contains($"<h1>{heading}</h1>", html);
        Assert.DoesNotContain(" style=", html);
        Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)[^>]*>", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("__RequestVerificationToken", html);
        Assert.Equal(noIndex, html.Contains("noindex", StringComparison.Ordinal));
        // Never the admin area's no-referrer: a browser would send "Origin: null" with the forms, and the same-origin check refuse them.
        Assert.Contains("<meta name=\"referrer\" content=\"same-origin\" />", html);
    }

    [Fact]
    public async Task The_registration_page_carries_its_guards_and_the_script_of_the_page()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        var home = await DynDnsPageTests.GetAsync(client, "/", ZoneHost);
        var form = Regex.Match(home, "<form id=\"register\"[^>]*>").Value;
        Assert.Contains("class=\"mat-form\"", form);
        Assert.Contains("novalidate", form);
        Assert.Contains("action=\"/dyndns/register\"", form);
        Assert.Contains("data-bits=\"16\"", form);
        Assert.Contains("name=\"website\"", home);
        Assert.Contains("class=\"msv-honeypot\" aria-hidden=\"true\"", home);
        Assert.Matches("<script src=\"/js/dyndns.js\\?v=[^\"]+\"></script>", home);
        Assert.Matches("<script src=\"/matui/matui.js\\?v=[^\"]+\"></script>", home);

        // The script is a file of the web root, plain JavaScript, served on every host.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/js/dyndns.js");
        request.Headers.Host = ZoneHost;
        var script = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Contains("crypto.subtle", await script.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_no_form_takes_is_refused()
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/dyndns/register")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/dyndns/manage/release")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await DynDnsPageTests.PostFormAsync(client, "/dyndns", new() { ["name"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dyndns/nothing")).StatusCode);
    }

    [Theory]
    [InlineData("/dyndns/register")]
    [InlineData("/dyndns/ports")]
    [InlineData("/dyndns/manage")]
    [InlineData("/dyndns/manage/update")]
    [InlineData("/dyndns/manage/ports")]
    [InlineData("/dyndns/manage/release")]
    public async Task A_form_from_another_site_is_not_allowed(string path)
    {
        await using var service = await RunningService.StartAsync();
        using var client = service.Client();
        var answer = await DynDnsPageTests.PostFormAsync(client, path, new() { ["name"] = "somebody", ["key"] = "mos_x", ["confirm"] = "yes" },
            origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        var html = await answer.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Not allowed</h1>", html);
        Assert.Contains("This form can only be sent from this site.", html);
        Assert.Contains("frame-ancestors 'none'", answer.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_admin_area_is_not_on_the_zone_even_with_its_static_files_there()
    {
        await using var service = await RunningService.StartAsync(
            "--Services:PublicUrl=http://127.0.0.1", "--Services:AdminEmail=ops@example.org", "--Services:AdminPassword=a-long-test-password");
        using var client = service.Client();
        foreach (var path in new[] { "/admin", "/admin/SignIn", "/admin/api/tunnels" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = ZoneHost;
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        }

        using var toolkit = new HttpRequestMessage(HttpMethod.Get, "/matui/matui.css");
        toolkit.Headers.Host = ZoneHost;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(toolkit)).StatusCode);
    }
}
