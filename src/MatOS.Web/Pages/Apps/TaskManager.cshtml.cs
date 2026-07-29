using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class TaskManagerModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }
    public void OnGet() { }
}
