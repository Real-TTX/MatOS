using System.ComponentModel.DataAnnotations;
using MatOS.Core.Apps;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

/// <summary>Creates a shared link (the <c>Anonymous</c> role): one app, reachable by anyone who has the link.</summary>
public class ShareModel : PageModel
{
    private readonly AppService _apps;
    private readonly MatOSDbContext _db;

    public ShareModel(AppService apps, MatOSDbContext db)
    {
        _apps = apps;
        _db = db;
    }

    public InstalledApp App { get; private set; } = null!;

    [BindProperty]
    [StringLength(200)]
    public string? Label { get; set; }

    /// <summary>Days until the link stops working; 0 = never.</summary>
    [BindProperty]
    public int ExpiresInDays { get; set; } = 7;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null || app.UiService is null)
        {
            return NotFound();
        }

        App = app;
        return Page();
    }

    [TempData] public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        var app = await _apps.GetAsync(id, HttpContext.RequestAborted);
        if (app is null || app.UiService is null)
        {
            return NotFound();
        }

        _db.ShareLinks.Add(new ShareLink
        {
            AppId = app.Id,
            Token = Guid.NewGuid().ToString("N"),
            Label = Label?.Trim() ?? string.Empty,
            ExpiresAt = ExpiresInDays > 0 ? DateTime.UtcNow.AddDays(ExpiresInDays) : null,
        });
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        StatusMessage = "The link was created.";
        return RedirectToPage("/Apps/Details", new { id, tab = "sharing" });
    }
}
