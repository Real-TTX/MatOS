using System.Text.Encodings.Web;
using MatOS.Services.Api;
using MatOS.Services.Configuration;

namespace MatOS.Services.Pages;

/// <summary>
/// The public page of the service, which says what this is (the admin area is Razor Pages below /admin, Pages/Admin). Plain HTML from
/// code until it moves into the stack of the admin area too.
/// </summary>
public static class StatusPages
{
    /// <summary>The repository that holds MatOS and this service (called Mattiverse until 2026-11; the older matOS desktop is Real-TTX/MatOS-legacy).</summary>
    public const string RepositoryUrl = "https://github.com/Real-TTX/MatOS";

    private const string Style = """
        body{font-family:system-ui,sans-serif;max-width:60rem;margin:3rem auto;padding:0 1rem;color:#1d1d1f;line-height:1.5}
        h1{font-size:1.5rem;margin-bottom:.25rem}p.sub{color:#666;margin-top:0}a{color:#5b4bd8}
        table{border-collapse:collapse;width:100%;font-size:.9rem}th,td{text-align:left;padding:.4rem .5rem;border-bottom:1px solid #ddd}
        th{color:#555;font-weight:600}.yes{color:#137333}.no{color:#a50e0e}code{background:#f3f3f5;padding:0 .25rem;border-radius:4px}
        @media (prefers-color-scheme:dark){body{background:#111;color:#eee}th,td{border-color:#333}p.sub,th{color:#aaa}code{background:#222}a{color:#a99cff}}
        """;

    public static void MapStatusPages(this WebApplication app)
    {
        app.MapGet("/", Index);
        // The caller's own address as plain text: what ddclient (use=web) and scripts ask before an update.
        app.MapGet("/ip", (HttpContext context) => Results.Text(context.ClientIp().ToString())).RequireRateLimiting(InstanceApi.NamesPolicy);
        app.MapGet("/healthz", () => Results.Text("ok"));
    }

    private static IResult Index(HttpContext context, ServicesOptions options, FormGuard guard)
    {
        // The zone itself (my.mattiverse.de) is the public DynDNS page.
        if (DynDnsPages.IsZoneHost(context, options))
        {
            return DynDnsPages.Home(context, options, guard);
        }

        var zone = HtmlEncoder.Default.Encode(options.Zone);
        var body = $"""
            <h1>MatOS Services</h1>
            <p class="sub">Free addresses for <a href="{RepositoryUrl}">MatOS</a>.</p>
            <p>A MatOS installation without a domain of its own gets an address here, such as <code>anna.{zone}</code>, with its apps at
            <code>&lt;app&gt;.anna.{zone}</code>. It is chosen in the setup assistant of MatOS; there is nothing to do on this page.</p>
            <p>Without MatOS: anybody can get a free DynDNS name for a router or a script at <a href="https://{zone}">https://{zone}</a>.</p>
            <p>The service runs. Installations reach it at <code>{HtmlEncoder.Default.Encode(options.PublicUrl)}</code>.</p>
            """;
        return Page("MatOS Services", body);
    }

    private static IResult Page(string title, string body) => new HtmlPage($"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex"><title>{title}</title><style>{Style}</style></head>
        <body>{body}</body></html>
        """);

    /// <summary>An HTML answer that cannot be framed or cached and loads nothing from elsewhere.</summary>
    private sealed class HtmlPage(string html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var headers = httpContext.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers.CacheControl = "no-store";
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            return httpContext.Response.WriteAsync(html);
        }
    }
}
