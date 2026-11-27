using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin;

/// <summary>
/// The tunnels connected right now, longest first (refreshed in place by js/services.js from <c>/admin/api/tunnels</c>), and the ones that ended
/// last.
/// </summary>
public sealed class TunnelsModel(TunnelRegistry registry, UsageStats stats, TimeProvider time) : PageModel
{
    /// <summary>How many ended tunnels the page lists.</summary>
    public const int Recent = 25;

    /// <summary>How often the page asks for the live list, in seconds.</summary>
    public const int RefreshSeconds = 5;

    public IReadOnlyList<LiveTunnel> Live { get; private set; } = [];

    public IReadOnlyList<TunnelSessionEntry> Ended { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public async Task OnGetAsync()
    {
        Now = time.GetUtcNow();
        Live = LiveTunnels.Of(registry, Now);
        Ended = await stats.SessionsAsync(null, Recent, HttpContext.RequestAborted);
    }
}
