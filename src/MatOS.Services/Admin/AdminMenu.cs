namespace MatOS.Services.Admin;

/// <summary>An entry of the menu of the admin area.</summary>
public sealed record AdminMenuItem(string Title, string Href, string Icon);

/// <summary>The one menu of the admin area (the layout draws it; the account and the sign-out are at its foot).</summary>
public static class AdminMenu
{
    public static readonly IReadOnlyList<AdminMenuItem> Items =
    [
        new("Overview", "/admin", "dashboard"),
        new("Installations", "/admin/Installations", "server"),
        new("Tunnels", "/admin/Tunnels", "route"),
        new("Domains", "/admin/Domains", "globe"),
        new("Statistics", "/admin/Statistics", "bar-chart"),
        new("Users", "/admin/Users", "users"),
        new("Settings", "/admin/Settings", "settings"),
    ];

    /// <summary>The entry a path belongs to: the longest one it starts with (the details of an installation mark Installations).</summary>
    public static string ActiveHref(string path)
    {
        path = path.TrimEnd('/');
        return Items
            .Where(i => string.Equals(path, i.Href, StringComparison.OrdinalIgnoreCase)
                        || (i.Href != "/admin" && path.StartsWith(i.Href + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(i => i.Href)
            .FirstOrDefault() ?? (string.Equals(path, "/admin/Index", StringComparison.OrdinalIgnoreCase) ? "/admin" : "");
    }
}
