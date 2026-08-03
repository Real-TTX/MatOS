using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

// Unified properties window. `type` selects which top-level tabs show:
//   app       -> App | Stack | Container | Volumes
//   stack     -> Stack | Container | Volumes
//   container -> Container | Volumes
// `id` is a container id (app/container), `name` is a stack name (stack). Everything else
// is resolved client-side from the existing docker APIs, so this is the single code base.
public class PropertiesModel : PageModel
{
    public void OnGet() { }
}
