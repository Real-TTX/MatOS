using Mattiverse.Core.Catalog;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Apps;
using Mattiverse.Web.Infrastructure;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Apps;

/// <summary>
/// The "window" of an app. Like on a phone, exactly one app is open at a time and it fills the screen; the bar on top
/// takes you back. Apps that forbid being embedded are offered in a new tab instead.
/// </summary>
public class OpenModel : PageModel
{
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;
    private readonly FrameProbe _probe;
    private readonly Mattiverse.Core.Integrations.PersonAccess _person;

    public OpenModel(AppService apps, CatalogService catalog, AppUrls urls, FrameProbe probe, Mattiverse.Core.Integrations.PersonAccess person)
    {
        _apps = apps;
        _catalog = catalog;
        _urls = urls;
        _probe = probe;
        _person = person;
    }

    public InstalledApp App { get; private set; } = null!;
    public string? Icon { get; private set; }
    public string? OpenUrl { get; private set; }
    public FrameCheck Frame { get; private set; } = new(false, null);

    /// <param name="id">The app.</param>
    /// <param name="path">An address of the app to open instead of its start page (a launcher or an entry of its menu on the home screen).</param>
    public async Task<IActionResult> OnGetAsync(long id, string? path = null)
    {
        var ct = HttpContext.RequestAborted;
        var app = await _apps.GetAsync(id, ct);
        if (app is null)
        {
            return NotFound();
        }

        // MatIdentity would turn the person away at the app anyway; the window does not even open it.
        if (!User.IsAdmin() && await _person.ForUserAsync(User.GetUserId(), ct) is { } person && !person.MayUse(app))
        {
            return Forbid();
        }

        // An app that is still installing (or failed) has nothing to show yet: go to its page.
        // Without a page to open it is the start of the app, or its sign-in while MatIdentity signs people in (nobody clicks "Sign in" first).
        var url = await _urls.OpenUrlAsync(app, Request, ct, Mattiverse.Core.Integrations.ShellRules.IsPath(path) ? path : await _apps.StartPathAsync(app, ct));
        if (app.Lifecycle != AppLifecycle.Installed || url is null)
        {
            return RedirectToPage("/Apps/Details", new { id });
        }

        App = app;
        Icon = _catalog.IconOf(app.CatalogId);
        OpenUrl = url;
        Frame = await _probe.CheckAsync(app, url, $"{Request.Scheme}://{Request.Host}", ct);
        return Page();
    }
}
