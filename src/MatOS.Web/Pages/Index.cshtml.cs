using MatOS.Web.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

public class IndexModel : PageModel
{
    public record SystemApp(string Key, string Title, string Url, string Icon, bool AdminOnly);

    private static readonly SystemApp[] Apps =
    {
        new("task-manager", "Task Manager", "/apps/task-manager", "tasks", false),
        new("files", "Files", "/apps/files", "files", true),
        new("backups", "Backups", "/apps/backups", "backups", true),
        new("network", "Network", "/apps/network", "network", true),
        new("store", "Store", "/apps/store", "store", false),
        new("users", "Users", "/apps/users", "users", true),
        new("settings", "Settings", "/apps/settings", "settings", false),
    };

    public IEnumerable<SystemApp> VisibleApps => Apps.Where(a => !a.AdminOnly || User.IsAdmin());

    public void OnGet() { }
}
