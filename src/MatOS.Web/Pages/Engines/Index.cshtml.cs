using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Engines;

/// <summary>
/// One engine in the window of the home screen: its state, what it keeps for MatOS and which of your apps it serves, and its own pages: here in the
/// window when the engine allows MatOS to frame it (MAT_FRAME_ANCESTORS, Support/FrameProbe.cs), else in a new tab.
/// </summary>
public class IndexModel : PageModel
{
    private readonly EngineOverview _engines;
    private readonly AppService _apps;
    private readonly FrameProbe _probe;

    public IndexModel(EngineOverview engines, AppService apps, FrameProbe probe)
    {
        _engines = engines;
        _apps = apps;
        _probe = probe;
    }

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    public EngineRow Engine { get; private set; } = null!;

    public IReadOnlyList<(InstalledApp App, string What)> Served { get; private set; } = [];

    /// <summary>Whether the engine's own pages can be shown in the window here (<c>null</c>: it is not installed or has no web page).</summary>
    public FrameCheck? Frame { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrEmpty(Id))
        {
            return RedirectToPage("/Settings/Engines");
        }

        var ct = HttpContext.RequestAborted;
        var engine = await _engines.GetAsync(Id, Request, ct);
        if (engine is null)
        {
            return NotFound();
        }

        Engine = engine;
        Served = _engines.ServedBy(engine.Catalog.Id, await _apps.ListAsync(ct));
        if (engine.Installed && engine.OpenUrl is not null)
        {
            Frame = await _probe.CheckAsync(engine.App!, engine.OpenUrl, $"{Request.Scheme}://{Request.Host}", ct);
        }

        return Page();
    }
}
