using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using Microsoft.AspNetCore.Antiforgery;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The endpoints of the screen of the choices (docs/ARCHITECTURE.md "The first start"): the public addresses MatOS Services saw (the records of an own domain
/// made by hand) and the test of the answers (the card "Test"). Administrators only, and only while the first start is not past the choices: afterwards they
/// answer <c>409 {redirect}</c> with the screen it goes on at. The screen of the mail has two more: MatSMTP's state and the check of its account.
/// </summary>
public static class SetupEndpoints
{
    public const string IpPath = "/api/setup/ip";
    public const string TestPath = "/api/setup/test";
    public const string MailPath = "/api/setup/mail";
    public const string MailTestPath = "/api/setup/mail/test";

    /// <summary>How often the mail account may be checked (per process): once in this time.</summary>
    public static readonly TimeSpan MailTestPause = TimeSpan.FromSeconds(30);

    /// <summary>When the mail account was checked last (ticks of UTC), shared by every request of this process.</summary>
    private static long _lastMailTest;

    /// <summary>What the card posts (the answers of the tree as they are now).</summary>
    public sealed record TestBody(string? Reach, string? Name, string? Domain, string? Provider, string? IpMode, string? FixedIp, string? Cname);

    public static void MapSetupApi(this WebApplication app)
    {
        // GET /api/setup/ip -> {ipv4, ipv6}: from the one cached check of the ports (SetupService.ProbeAsync); null when MatOS Services could not say.
        app.MapGet(IpPath, async (HttpContext http, SetupService setup, SystemConfigService config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (Past(await config.GetAsync(ct)) is { } past)
            {
                return past;
            }

            var probe = await setup.ProbeAsync(ct);
            var seen = probe.Ok ? probe.Value : null;
            return Results.Json(new { ipv4 = seen?.Ipv4, ipv6 = seen?.Ipv6 });
        }).RequireAuthorization("AdminOnly");

        // POST /api/setup/test (the anti-forgery token of the page in the header RequestVerificationToken), body {reach, name, domain, provider, ipMode,
        // fixedIp, cname} -> {rows: [{key, label, state, advisory, text}], canContinue, ipv4, ipv6}.
        app.MapPost(TestPath, async (HttpContext http, IAntiforgery antiforgery, SetupService setup, SystemConfigService config, TestBody? body, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (Past(await config.GetAsync(ct)) is { } past)
            {
                return past;
            }

            var input = body ?? new TestBody(null, null, null, null, null, null, null);
            var result = await setup.TestAsync(new SetupTestInput(input.Reach, input.Name, input.Domain, input.Provider, input.IpMode, input.FixedIp, input.Cname), ct);
            return Results.Json(new
            {
                rows = result.Rows.Select(r => new { key = r.Key, label = r.Label, state = r.State, advisory = r.Advisory, text = r.Text }),
                canContinue = result.CanContinue,
                ipv4 = result.Ipv4,
                ipv6 = result.Ipv6,
                summary = SetupPlan.Summary(ChoicesOf(input)),
            });
        }).RequireAuthorization("AdminOnly");

        // GET /api/setup/mail -> {installed, answers, accounts, healthLevel, healthText, accountsUrl, canTest}: MatSMTP for the screen of the mail.
        app.MapGet(MailPath, async (HttpContext http, MailSetup mail, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var s = await mail.StatusAsync(ct);
            return Results.Json(new
            {
                installed = s.Installed,
                answers = s.Answers,
                accounts = s.Accounts,
                healthLevel = s.HealthLevel,
                healthText = s.HealthText,
                accountsUrl = s.AccountsUrl,
                canTest = s.CanTest,
            });
        }).RequireAuthorization("AdminOnly");

        // POST /api/setup/mail/test (the anti-forgery token in the header RequestVerificationToken) -> {ok, message}: "Check the mail account", at most once in
        // 30 seconds for the whole process (a second click, a second tab: "Checked just now.").
        app.MapPost(MailTestPath, async (HttpContext http, IAntiforgery antiforgery, MailSetup mail, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (!MayCheckMail(DateTime.UtcNow))
            {
                return Results.Json(new { ok = false, message = "Checked just now." });
            }

            var check = await mail.TestAsync(ct);
            return Results.Json(new { ok = check.Ok, message = check.Message });
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Whether the mail account may be checked now (and takes the turn): not within <see cref="MailTestPause"/> of the last check.</summary>
    private static bool MayCheckMail(DateTime now)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastMailTest);
            if (last != 0 && now.Ticks - last < MailTestPause.Ticks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastMailTest, now.Ticks, last) == last)
            {
                return true;
            }
        }
    }

    /// <summary>The answers of the card as choices, for the sentence of what "Set up the rest" will do (<see cref="SetupPlan.Summary"/>).</summary>
    private static SetupChoices ChoicesOf(TestBody input)
    {
        var reach = input.Reach ?? string.Empty;
        var withProvider = reach == Reach.Domain && DnsProviders.Find(input.Provider) is not null;
        var ip = withProvider ? DnsIp.FromForm(input.IpMode, input.FixedIp).Ip ?? DnsIp.Default : null;
        return new SetupChoices(reach, input.Name, input.Domain, null, ip, Provider: input.Provider, IpMode: withProvider ? input.IpMode ?? DnsIp.Unsure : DnsIp.Unsure);
    }

    /// <summary>The first start is past the choices (the way is confirmed, or MatOS was set up before): 409 with the screen it goes on at.</summary>
    private static IResult? Past(SystemConfig config)
        => config.WizardCompleted || SetupPlan.Continued(config.Setup)
            ? Results.Json(new { redirect = SetupFlow.NextPage(config) }, statusCode: StatusCodes.Status409Conflict)
            : null;
}
