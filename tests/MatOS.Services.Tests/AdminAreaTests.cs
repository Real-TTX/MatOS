using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using MatOS.Services.Admin;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Services.Tests;

/// <summary>The admin area (/admin): the accounts, the sign-in and its guards, where it answers, and blocking a name.</summary>
public partial class AdminAreaTests
{
    private const string Email = "ops@example.org";
    private const string Password = "a-long-test-password";

    private static readonly string[] Pages =
    [
        "/admin", "/admin/Installations", "/admin/Tunnels", "/admin/Domains", "/admin/Statistics", "/admin/Users", "/admin/Users/Edit",
        "/admin/Settings", "/admin/Account", "/admin/Account?tab=password",
    ];

    [Fact]
    public void The_first_administrator_needs_an_address_and_a_password()
    {
        static IReadOnlyList<string> Problems(string? email, string? password)
        {
            var options = new ServicesOptions { AdminEmail = email, AdminPassword = password };
            options.Normalize();
            return options.Problems();
        }

        Assert.Empty(Problems(null, null));
        Assert.Empty(Problems(Email, Password));
        Assert.Contains(Problems(null, Password), p => p.Contains("AdminEmail"));
        Assert.Contains(Problems(Email, null), p => p.Contains("AdminEmail"));
        Assert.Contains(Problems("not-an-address", Password), p => p.Contains("e-mail address"));
        Assert.Contains(Problems(Email, "short"), p => p.Contains("8 characters"));

        var below = new ServicesOptions { PublicUrl = "https://admin.my.mattiverse.de" };
        below.Normalize();
        Assert.Contains(below.Problems(), p => p.Contains("PublicUrl"));
    }

    [Fact]
    public void Wrong_passwords_lock_an_address_for_a_while_whether_it_has_an_account_or_not()
    {
        var clock = new ManualClock();
        using var guard = new AdminGuard(clock);
        for (var i = 0; i < AdminGuard.MaxFailures - 1; i++)
        {
            guard.Failed("Somebody@Example.org");
        }

        Assert.Null(guard.LockedUntil("somebody@example.org"));
        guard.Failed("somebody@example.org");
        Assert.Equal(clock.GetUtcNow() + AdminGuard.LockFor, guard.LockedUntil(" SOMEBODY@example.org "));
        Assert.Null(guard.LockedUntil("other@example.org"));

        clock.Advance(AdminGuard.LockFor);
        Assert.Null(guard.LockedUntil("somebody@example.org"));

        // Failures long apart never add up to a lock.
        guard.Failed("slow@example.org");
        clock.Advance(AdminGuard.LockFor + TimeSpan.FromMinutes(1));
        for (var i = 0; i < AdminGuard.MaxFailures - 1; i++)
        {
            guard.Failed("slow@example.org");
        }

        Assert.Null(guard.LockedUntil("slow@example.org"));
        guard.Succeeded("slow@example.org");
        Assert.Null(guard.LockedUntil("slow@example.org"));
    }

    [Fact]
    public async Task The_sign_in_needs_the_token_of_the_page_and_opens_every_page()
    {
        await using var service = await StartAsync();
        var admin = new Browser(service);

        var first = await admin.GetAsync("/admin/Installations");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.StartsWith("/admin/SignIn", first.Headers.Location!.OriginalString.Replace(service.BaseUri.ToString(), "/"));

        // Without the antiforgery token the form is refused before the password is looked at.
        var forged = await admin.Client.PostAsync("/admin/SignIn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = Email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        Assert.True(await admin.SignInAsync(Email, Password));
        var cookie = admin.Cookies.GetCookies(new Uri(service.BaseUri, "/admin/")).Single(c => c.Name == "matos-services.auth");
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/admin", cookie.Path);

        foreach (var page in Pages)
        {
            var answer = await admin.GetAsync(page);
            Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{page}: {answer.StatusCode}");
            var html = await answer.Content.ReadAsStringAsync();
            Assert.Contains("mat-nav__item", html);
            Assert.Contains("noindex", html);
            Assert.Contains("frame-ancestors 'none'", answer.Headers.GetValues("Content-Security-Policy").Single());
        }

        // The sign-out ends the session on the server: the old cookie opens nothing any more.
        var stolen = admin.Cookies.GetCookies(new Uri(service.BaseUri, "/admin/")).Single(c => c.Name == "matos-services.auth").Value;
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync("/admin", "/admin/SignOut", [])).StatusCode);
        var replay = new Browser(service);
        replay.Cookies.Add(service.BaseUri, new Cookie("matos-services.auth", stolen, "/admin"));
        Assert.Equal(HttpStatusCode.Redirect, (await replay.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_sign_in()
    {
        await using var service = await StartAsync();
        var attacker = new Browser(service);
        for (var i = 0; i < AdminGuard.MaxFailures; i++)
        {
            Assert.False(await attacker.SignInAsync(Email, "wrong-password-" + i));
        }

        // Locked: even the right password does not get in now.
        var owner = new Browser(service);
        Assert.False(await owner.SignInAsync(Email, Password));
        Assert.Contains("Too many wrong passwords", owner.LastPage);
    }

    [Fact]
    public async Task The_admin_area_answers_only_on_the_host_of_the_api()
    {
        await using var service = await StartAsync();
        var admin = new Browser(service);
        Assert.True(await admin.SignInAsync(Email, Password));

        foreach (var host in new[] { RunningService.Zone, "anna." + RunningService.Zone, "app.anna." + RunningService.Zone, "elsewhere.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
            request.Headers.Host = host;
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task A_blocked_name_is_refused_everywhere_until_it_is_unblocked()
    {
        await using var service = await StartAsync();
        var key = await service.RegisterAsync("berta");
        await using (var tunnel = await FakeTunnelClient.ConnectAsync(service.BaseUri, key, _ => new IPEndPoint(IPAddress.Loopback, 9)))
        {
            await service.WaitForTunnelAsync("berta");
            var admin = new Browser(service);
            Assert.True(await admin.SignInAsync(Email, Password));
            var details = "/admin/Installations/Details?name=berta";
            Assert.Contains("Connected since", await admin.GetStringAsync(details));

            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync(details, details + "&handler=Block", new() { ["Reason"] = "phishing" })).StatusCode);

            // The tunnel went with the block, and comes not back.
            Assert.Null(service.Registry.Get("berta"));
            using (var again = new ClientWebSocket())
            {
                again.Options.SetRequestHeader("Authorization", "Bearer " + key);
                var refused = await Assert.ThrowsAsync<WebSocketException>(() =>
                    again.ConnectAsync(new Uri(new UriBuilder(service.BaseUri) { Scheme = "ws" }.Uri, "/api/v1/tunnel"), CancellationToken.None));
                Assert.Contains("403", refused.Message);
            }

            Assert.Equal(DnsRcode.NxDomain, (await DynDnsPageTests.AskAsync(service.Dns, "app.berta.my.mattiverse.de", DnsType.A)).Rcode);

            using var dyndns = service.Client();
            dyndns.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"berta:{key}")));
            Assert.Equal("abuse", await dyndns.GetStringAsync("/nic/update?hostname=berta.my.mattiverse.de&myip=203.0.113.9"));

            using var anonymous = service.Client();
            var name = await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/berta");
            Assert.Equal((false, "blocked"), (name!.Available, name.Reason));
            var register = await anonymous.PostAsJsonAsync("/api/v1/instances", new { name = "berta" });
            Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);
            Assert.Equal("blocked", (await register.Content.ReadFromJsonAsync<ApiError>())!.Error);

            // Its owner cannot set it free by giving it back.
            using var owner = service.Client(key);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync("/api/v1/instance")).StatusCode);

            var page = await admin.GetStringAsync(details);
            Assert.Contains("phishing", page);
            Assert.Contains("Blocked the name", page);
            Assert.Contains(Email, page);

            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync(details, details + "&handler=Unblock", [])).StatusCode);
            Assert.Equal(DnsRcode.NoError, (await DynDnsPageTests.AskAsync(service.Dns, "app.berta.my.mattiverse.de", DnsType.A)).Rcode);

            // Given back by an administrator: the name is free again, and the history stays.
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync(details, details + "&handler=GiveBack", [])).StatusCode);
            Assert.True((await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/berta"))!.Available);
        }

        using var scope = service.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var actions = await db.AdminActions.Where(a => a.InstanceName == "berta").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal([AdminActions.Block, AdminActions.Unblock, AdminActions.GiveBack], actions);
        Assert.All(await db.AdminActions.ToListAsync(), a => Assert.Equal(Email, a.UserEmail));
        Assert.All(await db.AdminActions.ToListAsync(), a => Assert.NotNull(a.CreateUserId));
    }

    [Fact]
    public async Task The_first_administrator_is_created_once()
    {
        await using var service = await StartAsync();
        using var scope = service.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.Equal((Email, UserRole.Admin, UpdateState.Created), (user.Email, user.Role, user.UpdateState));
        Assert.StartsWith("pbkdf2$sha256$", user.PasswordHash);

        // Another start with other values finds the account and leaves it alone.
        await AdminAccounts.EnsureFirstAsync(service.App.Services, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    private static Task<RunningService> StartAsync() => RunningService.StartAsync(
        "--Services:PublicUrl=http://127.0.0.1", $"--Services:AdminEmail={Email}", $"--Services:AdminPassword={Password}");

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    /// <summary>A browser of the tests: keeps cookies, follows no redirect, and fills in the antiforgery token of the page it posts from.</summary>
    private sealed class Browser
    {
        public Browser(RunningService service)
        {
            Client = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false }) { BaseAddress = service.BaseUri };
        }

        public CookieContainer Cookies { get; } = new();

        public HttpClient Client { get; }

        public string LastPage { get; private set; } = "";

        public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

        public Task<string> GetStringAsync(string path) => Client.GetStringAsync(path);

        /// <summary>True when the sign-in let the browser in (a redirect into the admin area).</summary>
        public async Task<bool> SignInAsync(string email, string password)
        {
            var answer = await PostFormAsync("/admin/SignIn", "/admin/SignIn", new() { ["Input.Email"] = email, ["Input.Password"] = password });
            LastPage = await answer.Content.ReadAsStringAsync();
            return answer.StatusCode == HttpStatusCode.Redirect;
        }

        public async Task<HttpResponseMessage> PostFormAsync(string page, string action, Dictionary<string, string> fields)
        {
            var html = await Client.GetStringAsync(page);
            fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(TokenRegex().Match(html).Groups[1].Value);
            return await Client.PostAsync(action, new FormUrlEncodedContent(fields));
        }
    }
}
