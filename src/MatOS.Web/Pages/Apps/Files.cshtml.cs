using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class FilesModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Volume { get; set; }
    [BindProperty(SupportsGet = true)] public string? Path { get; set; }
    public void OnGet() { }
}
