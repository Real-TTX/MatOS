using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class StackSettingsModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    public void OnGet() { }
}
