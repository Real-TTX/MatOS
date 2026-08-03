using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

// Compatibility redirect to the unified /apps/properties window.
public class StackSettingsModel : PageModel
{
    public IActionResult OnGet(string name) => RedirectToPage("Properties", new { type = "stack", name });
}
