using MatOS.Services.Tunnel;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin;

/// <summary>The tunnels connected right now, longest first.</summary>
public sealed class TunnelsModel(TunnelRegistry registry, TimeProvider time) : PageModel
{
    public IReadOnlyList<TunnelSession> Sessions { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public void OnGet()
    {
        Now = time.GetUtcNow();
        Sessions = registry.Sessions.OrderBy(s => s.ConnectedAt).ToList();
    }
}
