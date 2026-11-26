using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Web.Support;

/// <summary>Can the app be shown inside MatOS (an iframe)? If not: why not.</summary>
public sealed record FrameCheck(bool Embeddable, string? Reason);

/// <summary>
/// Looks at the response headers of an app to see whether it allows being embedded by the page of MatOS that asks (<c>Content-Security-Policy:
/// frame-ancestors</c>, which wins, else <c>X-Frame-Options</c>). The engines name MatOS in <c>frame-ancestors</c> when MatOS gave them its addresses
/// (<c>MAT_FRAME_ANCESTORS</c>, <see cref="FrameLink"/>). Apps that forbid it are opened in a new tab instead of showing a blank frame.
/// </summary>
public sealed class FrameProbe
{
    /// <summary>The name of the HTTP client of the probe: it follows no redirect by itself (one to the public address cannot be reached from inside Docker).</summary>
    public const string ClientName = "frame-probe";

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private readonly AppUrls _urls;
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;

    public FrameProbe(AppUrls urls, IHttpClientFactory http, IMemoryCache cache)
    {
        _urls = urls;
        _http = http;
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

        try
        {
            var client = _http.CreateClient(ClientName);
            client.Timeout = TimeSpan.FromSeconds(4);
            var target = new Uri(url);
            HttpResponseMessage? response = null;
            try
            {
                // Follow a redirect only while it stays on the address inside Docker (a sign-in page carries the headers that count).
                for (var hop = 0; hop < 4; hop++)
                {
                    response?.Dispose();
                    response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                    var location = response.Headers.Location;
                    if ((int)response.StatusCode is < 300 or >= 400 || location is null)
                    {
                        break;
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(target, location);
                    if (!string.Equals(next.Authority, target.Authority, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    target = next;
                }

                var xfo = response!.Headers.TryGetValues("X-Frame-Options", out var x) ? x : null;
                var csp = response.Headers.TryGetValues("Content-Security-Policy", out var c) ? c : null;
                var (allowed, reason) = FramePolicy.Decide(xfo, csp, pageOrigin, FrameLink.OriginOf(openUrl));
                return new FrameCheck(allowed, reason);
            }
            finally
            {
                response?.Dispose();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new FrameCheck(false, "The app does not answer right now. Is it running?");
        }
    }
}
