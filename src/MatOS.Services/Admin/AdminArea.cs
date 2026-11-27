using System.Net;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Admin;

/// <summary>
/// Where the admin area answers and how (docs/SERVICES.md "The admin area"): only below <c>/admin</c>, only on the host of
/// <see cref="ServicesOptions.PublicUrl"/> (never on the zone or a name below it, which belong to the installations and the DynDNS page), only
/// once an account exists, and with the headers of a page on the internet. Everywhere else <c>/admin</c> is 404, as if it did not exist.
/// </summary>
public sealed class AdminArea(ServicesOptions options, IHostEnvironment environment, IServiceScopeFactory scopes)
{
    public const string Path = "/admin";

    /// <summary>The rate limit of the sign-in, per address (an IPv6 address counts as its /48).</summary>
    public const string SignInPolicy = "admin-sign-in";

    /// <summary>The rate limit of every other request of the admin area, per address.</summary>
    public const string PagePolicy = "admin";

    /// <summary>Strict on purpose: the pages load the toolkit and nothing else, and have no inline script or style.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; form-action 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; object-src 'none'";

    // Once an account exists it stays so: the last one cannot be deleted (Users), so the answer is kept instead of asked again.
    private volatile bool _open;

    public static bool IsAdminPath(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The host of the API (<c>services.mattiverse.de</c>); in Development also the machine itself (<c>localhost</c>, a loopback address), where
    /// a <c>dotnet run</c> is opened.
    /// </summary>
    public bool IsAdminHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || options.IsZoneOrBelow(host))
        {
            return false;
        }

        if (host == options.PublicHost())
        {
            return true;
        }

        return environment.IsDevelopment()
               && (host == "localhost" || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
    }

    /// <summary>Whether an account exists (the first comes from the environment, or in Development the test account).</summary>
    public async Task<bool> IsOpenAsync(CancellationToken cancellationToken)
    {
        if (_open)
        {
            return true;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        _open = await db.Users.AnyAsync(cancellationToken);
        return _open;
    }

    /// <summary>An account was created.</summary>
    public void Opened() => _open = true;

    /// <summary>The gate in front of everything below <c>/admin</c>: 404 where it does not answer, and the headers where it does.</summary>
    public async Task GateAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAdminPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!IsAdminHost(context.Request.Host.Host) || !await IsOpenAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Robots-Tag"] = "noindex, nofollow";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        await next(context);
    }
}
