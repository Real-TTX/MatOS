using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class ContainerSettingsModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
    public void OnGet() { }
}
