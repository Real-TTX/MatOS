namespace MatOS.Web.Support;

/// <summary>A part of MatOS shown as an app on the home screen: the store, the backups, the settings ... It opens in the window of the home screen.</summary>
/// <param name="Name">The name under the icon (and the tooltip in the dock).</param>
/// <param name="Href">The page it opens in the window.</param>
/// <param name="Icon">An icon of the toolkit's sprite.</param>
/// <param name="Look">The colour of its icon: one of the <c>os-ico--*</c> classes of <c>os.css</c>.</param>
/// <param name="Count">A number on the corner of the icon (running tasks), or <c>null</c>.</param>
/// <param name="CountLabel">What the number means, for a screen reader.</param>
public sealed record SystemApp(string Name, string Href, string Icon, string Look, int? Count = null, string? CountLabel = null);

/// <summary>Which parts of MatOS the home screen shows as apps, for an administrator and for a user (the menus of <see cref="Navigation"/> as icons).</summary>
public static class Launcher
{
    /// <summary>The dock at the bottom: what is used most.</summary>
    /// <param name="updateNotice">What waits in Settings, Updates (a newer MatOS, security updates of this computer), or null: a mark on Settings (an administrator's) that leads there.</param>
    public static IReadOnlyList<SystemApp> Dock(bool isAdmin, string? updateNotice = null) => isAdmin
        ?
        [
            new("Store", "/Store", "store", "os-ico--store"),
            new("Apps", "/Apps", "apps", "os-ico--apps"),
            new("Backups", "/Backups", "save", "os-ico--backups"),
            updateNotice is not null
                ? new("Settings", "/Settings/Updates", "settings", "os-ico--settings", 1, updateNotice)
                : new("Settings", "/Settings", "settings", "os-ico--settings"),
        ]
        :
        [
            new("Apps", "/Apps", "apps", "os-ico--apps"),
            new("Account", "/Account/Profile", "user", "os-ico--account"),
        ];

    /// <summary>The others, as icons on the screen after the installed apps.</summary>
    public static IReadOnlyList<SystemApp> OnScreen(bool isAdmin, int activeJobs) => isAdmin
        ?
        [
            new("Activity", "/Jobs", "activity", "os-ico--activity", activeJobs > 0 ? activeJobs : null, activeJobs == 1 ? "task running" : "tasks running"),
            new("Task Manager", "/Tasks", "bar-chart", "os-ico--tasks"),
            new("Volumes", "/Volumes", "hard-drive", "os-ico--volumes"),
            new("People", "/People", "users", "os-ico--users"),
        ]
        : [];
}
