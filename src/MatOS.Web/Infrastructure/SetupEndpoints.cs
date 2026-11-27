using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using Microsoft.AspNetCore.Antiforgery;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The endpoints of the screen of the choices (docs/ARCHITECTURE.md "The first start"): the public addresses MatOS Services saw (the records of an own domain
/// made by hand) and the test of the answers (the card "Test"). Administrators only, and only while the first start is not past the choices: afterwards they
/// answer <c>409 {redirect}</c> with the screen it goes on at.
/// </summary>
public static class SetupEndpoints
{
    public const string IpPath = "/api/setup/ip";
    public const string TestPath = "/api/setup/test";

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
