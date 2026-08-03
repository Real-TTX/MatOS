using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

// Compatibility redirect to the unified /apps/properties window.
public class ContainerSettingsModel : PageModel
{
    public IActionResult OnGet(string id) => RedirectToPage("Properties", new { type = "container", id });
}
