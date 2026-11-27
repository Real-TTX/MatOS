using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Integrations;

/// <summary>
/// Every app in the window: the frame headers on Matcad's route of an app that does not take <c>MAT_FRAME_ANCESTORS</c> (the decision is
/// <see cref="FrameHeaders"/>). Written with every binding of <c>web.route</c> the broker makes or renews (an install, an update, a redeploy, connecting the
/// engines), for host routes and port routes alike, and only on a Matcad with the capability <c>responseHeaders</c> (0.13.0). They go with the route when the
/// app is removed, and come off when the app takes <c>MAT_FRAME_ANCESTORS</c> or sends a policy of its own.
/// </summary>
public sealed class RouteFrameHeaders
{
    /// <summary>The HTTP client that reads an app's answer: it follows no redirect by itself (one to the public address cannot be reached from inside Docker).</summary>
    public const string HttpClientName = "frame-probe";

    private readonly MatOSDbContext _db;
    private readonly MatcadClient _matcad;
    private readonly FrameLink _frames;
    private readonly CatalogService _catalog;
    private readonly AppAddresses _addresses;
    private readonly IHttpClientFactory _http;

    public RouteFrameHeaders(MatOSDbContext db, MatcadClient matcad, FrameLink frames, CatalogService catalog, AppAddresses addresses, IHttpClientFactory http)
    {
        _db = db;
        _matcad = matcad;
        _frames = frames;
        _catalog = catalog;
        _addresses = addresses;
        _http = http;
    }

    /// <summary>How often an app that does not answer is asked again (it may just have started), and the pause between.</summary>
    public static int Attempts { get; set; } = 3;

    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Brings the frame headers of the route of <paramref name="consumer"/> in line, after <paramref name="provider"/> bound its <c>web.route</c>. A failure is
    /// a warning: the app is reached all the same, it only opens in a new tab.
    /// </summary>
    public async Task ApplyAsync(InstalledApp consumer, InstalledApp provider, Action<string> log, CancellationToken ct)
    {
        if (provider.CatalogId != EngineIds.Matcad)
        {
            return;
        }

        try
        {
            if (!await _matcad.HasCapabilityAsync(MatcadClient.ResponseHeadersCapability, ct)
                || await _matcad.RouteOfBindingAsync(consumer.Slug, ct) is not { } route
                || !Owner.IsOurs(route["managedBy"]?.GetValue<string>()))
            {
                return;
            }

            // A route that allows every frame (a switch an administrator set in Matcad) needs nothing of MatOS.
            var takesOrigins = FrameHeaders.TakesOrigins(_catalog.Get(consumer.CatalogId));
            var allowEmbedding = route["allowEmbedding"]?.GetValue<bool>() == true;
            var wanted = takesOrigins || allowEmbedding
                ? []
                : FrameHeaders.Wanted(false, await ReadAnswerAsync(consumer, Attempts, ct), await _frames.OriginsAsync(ct));
            if (wanted is null)
            {
                log($"{consumer.Name} does not answer yet: whether its window may show it is settled the next time the engines are connected.");
                return;
            }

            var current = MatcadClient.ResponseHeadersOf(route);
            if (FrameHeaders.Merge(current, wanted) is not { } headers)
            {
                return;
            }

            await _matcad.SetResponseHeadersAsync(route, headers, ct);
            log(wanted.Count > 0
                ? $"Matcad lets MatOS show {consumer.Name} in its window ({FrameHeaders.ContentSecurityPolicy}: {wanted[^1].Value})."
                : $"Matcad leaves the frame headers of {consumer.Name} to the app.");
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: the frame headers of {consumer.Name} could not be set in Matcad: {ex.Message}");
        }
    }

    /// <summary>
    /// The response headers of the route of an app and whether the route allows every frame, as Matcad holds them: what it puts on the app's answers
    /// (<see cref="FrameHeaders.Through"/>). <c>null</c> when Matcad does not route the app or does not answer.
    /// </summary>
    public async Task<(IReadOnlyList<RouteHeader> Headers, bool AllowEmbedding)?> RouteOfAsync(InstalledApp app, CancellationToken ct)
    {
        var binding = await _db.AppBindings.AsNoTracking().Include(b => b.ProviderApp)
            .FirstOrDefaultAsync(b => b.ConsumerAppId == app.Id && b.Capability == Capabilities.WebRoute, ct);
        if (binding?.ProviderApp?.CatalogId != EngineIds.Matcad)
        {
            return null;
        }

        try
        {
            return await _matcad.RouteOfBindingAsync(app.Slug, ct) is { } route
                ? (MatcadClient.ResponseHeadersOf(route), route["allowEmbedding"]?.GetValue<bool>() == true)
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The frame headers of the app's own answer at its address inside Docker (not through the route), or <c>null</c> when it has no web page or does not answer.
    /// A redirect is followed while it stays on that address: a sign-in page carries the headers that count.
    /// </summary>
    /// <param name="url">Where to ask instead of the address inside Docker (the probe of a page during development asks the host port).</param>
    public async Task<FrameAnswer?> ReadAnswerAsync(InstalledApp app, int attempts, CancellationToken ct, string? url = null)
    {
        url ??= _addresses.InternalUrl(app) is { } inside ? inside + app.UiPath : null;
        if (url is null)
        {
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadAsync(new Uri(url), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= attempts)
                {
                    return null;
                }

                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private async Task<FrameAnswer> ReadAsync(Uri target, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(4);
        HttpResponseMessage? response = null;
        try
        {
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

            return new FrameAnswer(
                response!.Headers.TryGetValues(FrameHeaders.XFrameOptions, out var xfo) ? [.. xfo] : null,
                response.Headers.TryGetValues(FrameHeaders.ContentSecurityPolicy, out var csp) ? [.. csp] : null);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
