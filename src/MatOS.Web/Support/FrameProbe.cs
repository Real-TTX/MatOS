using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Web.Support;

/// <summary>Can the app be shown inside MatOS (an iframe)? If not: why not.</summary>
public sealed record FrameCheck(bool Embeddable, string? Reason);

/// <summary>
/// Looks at the response headers of an app to see whether it allows being embedded by the page of MatOS that asks (<c>Content-Security-Policy:
/// frame-ancestors</c>, which wins, else <c>X-Frame-Options</c>). The engines name MatOS in <c>frame-ancestors</c> when MatOS gave them its addresses
/// (<c>MAT_FRAME_ANCESTORS</c>, <see cref="FrameLink"/>); the route of any other app carries frame headers of MatOS (<see cref="RouteFrameHeaders"/>). The
/// answer is the one the browser gets: the app's own (at its address inside Docker; its public address cannot be reached from in here) with what Matcad's
/// route puts on it (<see cref="FrameHeaders.Through"/>). Apps that forbid it are opened in a new tab instead of showing a blank frame.
/// </summary>
public sealed class FrameProbe
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private readonly AppUrls _urls;
    private readonly RouteFrameHeaders _routes;
    private readonly IMemoryCache _cache;

    public FrameProbe(AppUrls urls, RouteFrameHeaders routes, IMemoryCache cache)
    {
        _urls = urls;
        _routes = routes;
        _cache = cache;
    }

    /// <summary>Whether the page at <paramref name="pageOrigin"/> (the MatOS the person uses) may show the app at <paramref name="openUrl"/> in a frame.</summary>
    public async Task<FrameCheck> CheckAsync(InstalledApp app, string openUrl, string pageOrigin, CancellationToken ct = default)
    {
        // A secure page (MatOS behind HTTPS) cannot show a plain-HTTP frame: the browser blocks it.
        if (pageOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && openUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return new FrameCheck(false, "The app is served over plain HTTP, which a secure page cannot embed. Give the app a domain with the reverse proxy to embed it here.");
        }

        var key = $"frame:{app.Slug}:{app.HostPort}:{app.PublicUrl}:{pageOrigin}";
        if (_cache.TryGetValue(key, out FrameCheck? cached) && cached is not null)
        {
            return cached;
        }

        var result = await ProbeAsync(app, openUrl, pageOrigin, ct);
        // Only remember positive answers: an app that is still starting must be checked again.
        if (result.Embeddable)
        {
            _cache.Set(key, result, CacheTime);
        }

        return result;
    }

    private async Task<FrameCheck> ProbeAsync(InstalledApp app, string openUrl, string pageOrigin, CancellationToken ct)
    {
        var url = await _urls.InternalUrlAsync(app, ct);
        if (url is null)
        {
            return new FrameCheck(false, "The app has no web interface.");
        }

        if (await _routes.ReadAnswerAsync(app, attempts: 1, ct, url) is not { } answer)
        {
            return new FrameCheck(false, "The app does not answer right now. Is it running?");
        }

        if (await _routes.RouteOfAsync(app, ct) is { } route)
        {
            answer = FrameHeaders.Through(answer, route.Headers, route.AllowEmbedding);
        }

        var (allowed, reason) = FramePolicy.Decide(answer.XFrameOptions, answer.ContentSecurityPolicies, pageOrigin, FrameLink.OriginOf(openUrl));
        return new FrameCheck(allowed, reason);
    }
}
