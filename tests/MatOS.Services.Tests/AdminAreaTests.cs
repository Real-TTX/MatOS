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
using MatOS.Services.Usage;
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

        // The form of the values stops only the creation of the first account, never the start (an older .env may have a short password).
        Assert.Empty(Problems("not-an-address", Password));
        Assert.Empty(Problems(Email, "short"));
        Assert.Contains("e-mail address", new ServicesOptions { AdminEmail = "not-an-address", AdminPassword = Password }.FirstAdminProblem());
        Assert.Contains("8 characters", new ServicesOptions { AdminEmail = Email, AdminPassword = "short" }.FirstAdminProblem());
        Assert.Null(new ServicesOptions { AdminEmail = Email, AdminPassword = Password }.FirstAdminProblem());

        var below = new ServicesOptions { PublicUrl = "https://admin.my.mattiverse.de" };
        below.Normalize();
        Assert.Contains(below.Problems(), p => p.Contains("PublicUrl"));
    }

    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.66");
    private static readonly IPAddress Owner = IPAddress.Parse("198.51.100.10");

    private static void Fail(AdminGuard guard, string email, IPAddress client)
    {
        using var attempt = guard.Begin(email, client);
        Assert.False(attempt.Refused);
        attempt.Failed();
    }

    [Fact]
    public void Wrong_passwords_lock_an_address_for_a_while_whether_it_has_an_account_or_not()
    {
        var clock = new ManualClock();
        var guard = new AdminGuard(clock);
        for (var i = 0; i < AdminGuard.MaxFailures - 1; i++)
        {
            Fail(guard, "Somebody@Example.org", Attacker);
        }

        Assert.Null(guard.LockedUntil("somebody@example.org", Attacker));
        Fail(guard, "somebody@example.org", Attacker);
        Assert.Equal(clock.GetUtcNow() + AdminGuard.LockFor, guard.LockedUntil(" SOMEBODY@example.org ", Attacker));
        Assert.True(guard.Begin("somebody@example.org", Attacker).Refused);
        Assert.Null(guard.LockedUntil("other@example.org", Attacker));

        // The lock is the attacker's network's: the owner signs in from elsewhere (an IPv6 /48 is one network).
        Assert.Null(guard.LockedUntil("somebody@example.org", Owner));
        using (var owner = guard.Begin("somebody@example.org", Owner))
        {
            Assert.False(owner.Refused);
            owner.Succeeded();
        }

        clock.Advance(AdminGuard.LockFor);
        Assert.Null(guard.LockedUntil("somebody@example.org", Attacker));

        // Failures long apart never add up to a lock.
        Fail(guard, "slow@example.org", Attacker);
        clock.Advance(AdminGuard.LockFor + TimeSpan.FromMinutes(1));
        for (var i = 0; i < AdminGuard.MaxFailures - 1; i++)
        {
            Fail(guard, "slow@example.org", Attacker);
        }

        Assert.Null(guard.LockedUntil("slow@example.org", Attacker));
        using (var right = guard.Begin("slow@example.org", Attacker))
        {
            right.Succeeded();
        }

        Assert.Null(guard.LockedUntil("slow@example.org", Attacker));

        var six = IPAddress.Parse("2001:db8:1:2::1");
        for (var i = 0; i < AdminGuard.MaxFailures; i++)
        {
            Fail(guard, "v6@example.org", IPAddress.Parse($"2001:db8:1:{i + 10}::{i + 1}"));
        }

        Assert.NotNull(guard.LockedUntil("v6@example.org", six));
    }

    [Fact]
    public void Guesses_sent_at_once_are_counted_before_their_passwords_are_checked()
    {
        var guard = new AdminGuard(new ManualClock());

        // Five guesses on their way, none checked yet: the sixth must wait, it cannot slip past the lock they may cause.
        var attempts = Enumerable.Range(0, AdminGuard.MaxFailures).Select(_ => guard.Begin(Email, Attacker)).ToList();
        Assert.All(attempts, a => Assert.False(a.Refused));
        Assert.True(guard.Begin(Email, Attacker).Refused);
        attempts.ForEach(a => a.Failed());
        Assert.NotNull(guard.LockedUntil(Email, Attacker));

        // A guess given back without an outcome (the request went away before the check) counts nothing.
        var other = new AdminGuard(new ManualClock());
        for (var i = 0; i < 10; i++)
        {
            other.Begin(Email, Attacker).Dispose();
        }

        Assert.Null(other.LockedUntil(Email, Attacker));

        // The same for everybody's limit: guesses in flight from many networks count against it before they are checked.
        var flood = new AdminGuard(new ManualClock());
        var many = Enumerable.Range(0, AdminGuard.MaxFailuresPerMinute)
            .Select(i => flood.Begin($"guess{i}@example.org", IPAddress.Parse($"203.0.113.{i + 1}"))).ToList();
        Assert.All(many, a => Assert.False(a.Refused));
        Assert.True(flood.Begin(Email, IPAddress.Parse("192.0.2.200")).Refused);
        many.ForEach(a => a.Dispose());
        Assert.False(flood.Begin(Email, IPAddress.Parse("192.0.2.200")).Refused);
    }

    [Fact]
    public void A_flood_of_wrong_passwords_keeps_out_unknown_networks_but_not_the_administrators()
    {
        var clock = new ManualClock();
        var guard = new AdminGuard(clock);

        // An administrator signed in from Owner once (or a session in the database says so at the start).
        using (var first = guard.Begin(Email, Owner))
        {
            first.Succeeded();
        }

        for (var i = 0; i < AdminGuard.MaxFailuresPerMinute; i++)
        {
            Fail(guard, $"guess{i}@example.org", IPAddress.Parse($"203.0.113.{i + 1}"));
        }

        var stranger = guard.Begin(Email, IPAddress.Parse("192.0.2.200"));
        Assert.True(stranger.Refused);
        Assert.Null(stranger.Until);
        using (var owner = guard.Begin(Email, Owner))
        {
            Assert.False(owner.Refused);
        }

        var remembered = IPAddress.Parse("192.0.2.77");
        guard.Remember(remembered, clock.GetUtcNow());
        using (var known = guard.Begin(Email, remembered))
        {
            Assert.False(known.Refused);
        }

        // The next minute lets everybody try again; a network not used for KnownFor is a stranger again.
        clock.Advance(TimeSpan.FromMinutes(1));
        using (var later = guard.Begin(Email, IPAddress.Parse("192.0.2.200")))
        {
            Assert.False(later.Refused);
        }

        clock.Advance(AdminGuard.KnownFor);
        for (var i = 0; i < AdminGuard.MaxFailuresPerMinute; i++)
        {
            Fail(guard, $"again{i}@example.org", IPAddress.Parse($"203.0.113.{i + 1}"));
        }

        Assert.True(guard.Begin(Email, remembered).Refused);
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
    public async Task Every_admin_page_sends_a_stranger_to_the_sign_in_and_opens_signed_in()
    {
        await using var service = await StartAsync();
        await service.RegisterAsync("carla");
        string[] pages =
        [
            .. Pages, "/admin/Installations/Details?name=carla", "/admin/Installations?q=carla&sort=name", "/admin/Statistics?period=7d",
            "/admin/Statistics?period=12m", "/admin?period=30d", "/admin/Users/Edit?id=1",
        ];

        var stranger = new Browser(service);
        foreach (var page in pages)
        {
            var answer = await stranger.GetAsync(page);
            Assert.True(answer.StatusCode == HttpStatusCode.Redirect, $"{page}: {answer.StatusCode}");
            Assert.StartsWith("/admin/SignIn", answer.Headers.Location!.OriginalString.Replace(service.BaseUri.ToString(), "/"));
        }

        // The JSON of the live tunnels has no sign-in page: 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/admin/api/tunnels")).StatusCode);

        var admin = new Browser(service);
        Assert.True(await admin.SignInAsync(Email, Password));
        foreach (var page in pages)
        {
            var answer = await admin.GetAsync(page);
            Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{page}: {answer.StatusCode}");
            var html = await answer.Content.ReadAsStringAsync();
            Assert.DoesNotContain(" style=", html);
            Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)[^>]*>", html);
            Assert.Contains("<meta name=\"robots\" content=\"noindex, nofollow\" />", html);
            Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\" />", html);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/api/tunnels")).StatusCode);
    }

    [Fact]
    public async Task Every_address_the_pages_make_is_lowercase_so_the_cookies_go_with_it()
    {
        await using var service = await StartAsync();
        await service.RegisterAsync("dora");
        var admin = new Browser(service);
        Assert.True(await admin.SignInAsync(Email, Password));

        // The cookies are scoped to /admin, and a browser matches that path by case: an address of /Admin/... would go without them.
        foreach (var page in new[] { "/admin", "/admin/Account", "/admin/Installations/Details?name=dora", "/admin/Users", "/admin/Users/Edit?id=1" })
        {
            var html = await admin.GetStringAsync(page);
            foreach (Match link in AddressRegex().Matches(html))
            {
                var address = WebUtility.HtmlDecode(link.Groups[1].Value);
                if (address.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(address.StartsWith("/admin", StringComparison.Ordinal), $"{page}: {address}");
                }
            }
        }

        // A form as the page renders it runs its handler, and its redirect lands on a page of the session, with the flash.
        var account = await admin.GetStringAsync("/admin/Account");
        var action = WebUtility.HtmlDecode(AddressRegex().Matches(account).Select(m => m.Groups[1].Value).First(a => a.Contains("handler=SignOutOthers", StringComparison.Ordinal)));
        var posted = await admin.PostFormAsync("/admin/Account", action, []);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        var location = posted.Headers.Location!.OriginalString;
        Assert.StartsWith("/admin/", location);
        var landed = await admin.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Contains("No other session was signed in.", await landed.Content.ReadAsStringAsync());

        var details = await admin.GetStringAsync("/admin/Installations/Details?name=dora");
        var block = WebUtility.HtmlDecode(AddressRegex().Matches(details).Select(m => m.Groups[1].Value).First(a => a.Contains("handler=Block", StringComparison.Ordinal)));
        Assert.Contains("name=dora", block);
        var blocked = await admin.PostFormAsync("/admin/Installations/Details?name=dora", block, new() { ["Reason"] = "spam" });
        Assert.StartsWith("/admin/", blocked.Headers.Location!.OriginalString);
        Assert.Contains("spam", await admin.GetStringAsync(blocked.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task A_short_password_of_an_older_version_stops_only_the_first_account()
    {
        await using var service = await RunningService.StartAsync(
            "--Services:PublicUrl=http://127.0.0.1", $"--Services:AdminEmail={Email}", "--Services:AdminPassword=short");

        // The service runs (the API answers), without an account and so without an admin area.
        using var client = service.Client();
        Assert.True((await client.GetFromJsonAsync<NameAvailability>("/api/v1/names/freename"))!.Available);
        using var scope = service.App.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ServicesDbContext>().Users.CountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin")).StatusCode);
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
    public async Task Wrong_current_passwords_count_like_wrong_sign_ins()
    {
        await using var service = await StartAsync();
        var thief = new Browser(service);
        Assert.True(await thief.SignInAsync(Email, Password));

        Dictionary<string, string> Change(string current) => new()
        {
            ["Input.CurrentPassword"] = current,
            ["Input.NewPassword"] = "another-long-password",
            ["Input.ConfirmPassword"] = "another-long-password",
        };

        for (var i = 0; i < AdminGuard.MaxFailures; i++)
        {
            var wrong = await thief.PostFormAsync("/admin/Account?tab=password", "/admin/Account", Change("wrong-password-" + i));
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        }

        // Locked: even the right current password is refused now, and the sign-in of the account too.
        var right = await thief.PostFormAsync("/admin/Account?tab=password", "/admin/Account", Change(Password));
        Assert.Contains("Too many wrong passwords", await right.Content.ReadAsStringAsync());
        Assert.False(await new Browser(service).SignInAsync(Email, Password));
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

            // Nor can an administrator while it is blocked (the block would go with the row).
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync(details, details + "&handler=GiveBack", [])).StatusCode);
            Assert.Equal("blocked", (await anonymous.GetFromJsonAsync<NameAvailability>("/api/v1/names/berta"))!.Reason);

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

        // An administrator's give-back is in the audit, not in the API's statistics.
        await service.App.Services.GetRequiredService<UsageRecorder>().FlushAsync(CancellationToken.None);
        Assert.Equal(0, (await db.ApiDays.SingleAsync()).GiveBacks);
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

    [GeneratedRegex("(?:action|href|formaction)=\"(/[^\"]*)\"")]
    private static partial Regex AddressRegex();

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
