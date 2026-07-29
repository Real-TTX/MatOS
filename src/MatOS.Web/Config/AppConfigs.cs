namespace MatOS.Web.Config;

/// <summary>Per-install desktop preferences (wallpaper, etc.). Persisted as desktop.json.</summary>
public class DesktopConfig
{
    public string Wallpaper { get; set; } = "aurora";
}

/// <summary>General system settings. Persisted as system.json.</summary>
public class SystemConfig
{
    /// <summary>Instance display name shown in the top bar / titles.</summary>
    public string InstanceName { get; set; } = "matOS";

    /// <summary>Base domain matOS uses to build per-app subdomains for the Caddy/Matcad proxy,
    /// e.g. apps.localhost (resolves to loopback locally) or a real wildcard domain in production.</summary>
    public string BaseDomain { get; set; } = "apps.localhost";
}

/// <summary>Built-in wallpapers (CSS gradient classes wp-*). Used by the desktop + Settings picker.</summary>
public static class Wallpapers
{
    public record Wallpaper(string Key, string Name);

    public static readonly IReadOnlyList<Wallpaper> All = new[]
    {
        new Wallpaper("aurora", "Aurora"),
        new Wallpaper("dusk", "Dusk"),
        new Wallpaper("ocean", "Ocean"),
        new Wallpaper("sunset", "Sunset"),
        new Wallpaper("forest", "Forest"),
        new Wallpaper("nebula", "Nebula"),
        new Wallpaper("graphite", "Graphite"),
    };

    public static bool IsValid(string key) => All.Any(w => w.Key == key);
    public static string Normalize(string? key) => key is not null && IsValid(key) ? key : "aurora";
}
