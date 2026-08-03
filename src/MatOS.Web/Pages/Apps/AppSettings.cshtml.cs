using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

// Compatibility redirect: settings are now the unified /apps/properties window. Keeps old
// links (and any client still running a pre-migration desktop.js) working.
public class AppSettingsModel : PageModel
{
    public IActionResult OnGet(string id) => RedirectToPage("Properties", new { type = "app", id });
}
