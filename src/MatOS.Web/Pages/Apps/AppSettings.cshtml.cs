using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class AppSettingsModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
    public void OnGet() { }
}
