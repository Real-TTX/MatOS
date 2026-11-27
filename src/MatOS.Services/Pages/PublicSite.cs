using MatOS.Services.Admin;
using MatOS.Services.Api;
using MatOS.Services.Configuration;

namespace MatOS.Services.Pages;

/// <summary>
/// The public side of the service (docs/SERVICES.md): the short page at <c>/</c>, the DynDNS pages (<c>/dyndns</c>, and the zone itself), the
/// caller's own address (<c>/ip</c>) and the health check. The pages are Razor Pages in the look of every Mat app (Pages/Index, Pages/DynDns,
/// the layout _Public); this class holds what they share and the two plain endpoints.
/// </summary>
public static class PublicSite
{
    /// <summary>The repository that holds MatOS and this service (called Mattiverse until 2026-11; the older matOS desktop is Real-TTX/MatOS-legacy).</summary>
    public const string RepositoryUrl = "https://github.com/Real-TTX/MatOS";

    /// <summary>Where the DynDNS pages live on every host (the zone itself shows the first of them at <c>/</c>).</summary>
    public const string DynDnsBase = "/dyndns";

    /// <summary>
    /// The rate limit of the public pages, per address: 120 page views and 60 forms a minute, as the name checks and the API allow (the
    /// budgets are the pages' own).
    /// </summary>
    public const string PagePolicy = "public-page";

    /// <summary>
    /// The policy of the public pages: the toolkit and the service's own script and nothing else, no inline script or style, no frames, forms
    /// only to this host. The same as the admin area's.
    /// </summary>
    public const string ContentSecurityPolicy = AdminArea.ContentSecurityPolicy;

    public static void MapPublicEndpoints(this WebApplication app)
    {
        // The caller's own address as plain text: what ddclient (use=web) and scripts ask before an update.
        app.MapGet("/ip", (HttpContext context) => Results.Text(context.ClientIp().ToString())).RequireRateLimiting(InstanceApi.NamesPolicy);
        app.MapGet("/healthz", () => Results.Text("ok"));
    }

    /// <summary>Whether a request is for the zone itself (<c>my.mattiverse.de</c>), where the DynDNS page is the start page.</summary>
    public static bool IsZoneHost(HttpContext context, ServicesOptions options) =>
        string.Equals(context.Request.Host.Host.TrimEnd('.'), options.Zone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>/</c> on the zone itself is the DynDNS page: the request goes on as <c>/dyndns</c> (before the routing), so both are the very same
    /// page. On any other host <c>/</c> is the short page of the service.
    /// </summary>
    public static Task ZoneStartAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == "/" && IsZoneHost(context, context.RequestServices.GetRequiredService<ServicesOptions>()))
        {
            context.Request.Path = DynDnsBase;
        }

        return next(context);
    }
}
