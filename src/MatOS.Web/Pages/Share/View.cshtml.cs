using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Share;

/// <summary>The page behind a shared link (role Anonymous): shows one app and a button that opens it. No account needed.</summary>
[AllowAnonymous]
public class ViewModel : PageModel
{
    private readonly MatOSDbContext _db;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;

    public ViewModel(MatOSDbContext db, CatalogService catalog, AppUrls urls)
    {
        _db = db;
        _catalog = catalog;
        _urls = urls;
    }

    public InstalledApp App { get; private set; } = null!;
    public string? Icon { get; private set; }
    public string? OpenUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        var ct = HttpContext.RequestAborted;
        var link = await _db.ShareLinks.Include(s => s.App).FirstOrDefaultAsync(s => s.Token == token, ct);

        // One answer for "unknown", "expired" and "app removed": do not reveal which links ever existed.
        if (link?.App is null || link.App.Lifecycle != AppLifecycle.Installed || (link.ExpiresAt is { } expires && expires <= DateTime.UtcNow))
        {
            return NotFound();
        }

        App = link.App;
        Icon = _catalog.IconOf(App.CatalogId);
        OpenUrl = await _urls.OpenUrlAsync(App, Request, ct);
        return Page();
    }
}
