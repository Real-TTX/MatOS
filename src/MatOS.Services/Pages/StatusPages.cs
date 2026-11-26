using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages;

/// <summary>
/// The two pages of the service: a public one that says what this is, and the administrator's list of installations. Plain HTML from
/// code: two pages do not justify Razor, and the service must stay one small image.
/// </summary>
public static class StatusPages
{
    public const string AdminPolicy = "admin";

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
        app.MapGet("/admin", AdminAsync).RequireRateLimiting(AdminPolicy);
        app.MapGet("/healthz", () => Results.Text("ok"));
    }

    private static IResult Index(ServicesOptions options)
    {
        var zone = HtmlEncoder.Default.Encode(options.Zone);
        var body = $"""
            <h1>MatOS Services</h1>
            <p class="sub">Free addresses for <a href="{RepositoryUrl}">MatOS</a>.</p>
            <p>A MatOS installation without a domain of its own gets an address here, such as <code>anna.{zone}</code>, with its apps at
            <code>&lt;app&gt;.anna.{zone}</code>. It is chosen in the setup assistant of MatOS; there is nothing to do on this page.</p>
            <p>The service runs. Installations reach it at <code>{HtmlEncoder.Default.Encode(options.PublicUrl)}</code>.</p>
            """;
        return Page("MatOS Services", body);
    }

    private static async Task<IResult> AdminAsync(
        HttpContext context, ServicesOptions options, ServicesDbContext db, TunnelRegistry registry, EdgeService edge, AdminGuard guard)
    {
        // Without a password there is no administrator view at all, rather than an open one.
        if (string.IsNullOrEmpty(options.AdminPassword))
        {
            return Results.NotFound();
        }

        if (guard.IsBlocked)
        {
            return Results.Text("Too many wrong passwords. Try again in a minute.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!DynDnsEndpoint.TryReadBasic(context.Request.Headers.Authorization.ToString(), out _, out var password)
            || !SamePassword(password, options.AdminPassword))
        {
            if (context.Request.Headers.Authorization.Count > 0)
            {
                // A browser's first request carries no credentials; only a password that was tried counts.
                guard.Failed();
            }

            context.Response.Headers.WWWAuthenticate = "Basic realm=\"MatOS Services admin\", charset=\"UTF-8\"";
            return Results.Text("Sign in with the administrator password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var instances = await db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync(context.RequestAborted);
        var sessions = registry.Sessions.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <h1>MatOS Services</h1>
            <p class="sub">{instances.Count} installations, {sessions.Count} tunnels connected, {edge.Connections} visitors at the edge.
            Zone <code>{E(options.Zone)}</code>.</p>
            <table><thead><tr><th>Name</th><th>Mode</th><th>Tunnel</th><th>IPv4</th><th>IPv6</th><th>Last seen</th><th>Created</th><th>From</th><th>Version</th></tr></thead><tbody>
            """);
        foreach (var i in instances)
        {
            var tunnel = sessions.TryGetValue(i.Name, out var s)
                ? $"<span class=\"yes\">connected</span> ({s.ActiveStreams} streams)"
                : "<span class=\"no\">no</span>";
            html.Append(CultureInfo.InvariantCulture, $"""
                <tr><td>{E(i.Name)}</td><td>{InstanceApi.ModeText(i.Mode)}</td><td>{tunnel}</td><td>{E(i.PublicIpv4)}</td><td>{E(i.PublicIpv6)}</td>
                <td>{Date(i.LastSeen)}</td><td>{Date(i.CreateDate)}</td><td>{E(i.CreatedFromIp)}</td><td>{E(i.Version)}</td></tr>
                """);
        }

        html.Append("</tbody></table>");
        return Page("MatOS Services - administration", html.ToString());
    }

    private static bool SamePassword(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    private static string Date(DateTime? value) =>
        value is null ? "" : value.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

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
