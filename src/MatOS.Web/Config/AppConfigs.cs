namespace MatOS.Web.Config;

/// <summary>Per-install desktop preferences (wallpaper, etc.). Persisted as desktop.json.</summary>
public class DesktopConfig
{
    public string Wallpaper { get; set; } = "aurora";
    /// <summary>UI theme: "auto" (follow OS via prefers-color-scheme), "dark" or "light".</summary>
    public string Theme { get; set; } = "auto";
}

/// <summary>General system settings. Persisted as system.json.</summary>
public class SystemConfig
{
    /// <summary>Instance display name shown in the top bar / titles.</summary>
    public string InstanceName { get; set; } = "matOS";

    /// <summary>Base domain matOS uses to build per-app subdomains for the Caddy/Matcad proxy,
    /// e.g. apps.localhost (resolves to loopback locally) or a real wildcard domain in production.</summary>
    public string BaseDomain { get; set; } = "apps.localhost";

    /// <summary>Docker network that app containers join so the reverse proxy (Caddy/Matcad) can
    /// reach them by name. Empty = the built-in default ("matos"). Set this to the network your
    /// existing Caddy/Matcad runs on if you don't use matOS's bundled proxy.</summary>
    public string Network { get; set; } = "";
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
        // Photo-style wallpapers: layered SVG scenes bundled locally, no external network.
        new Wallpaper("mountains", "Mountains"),
        new Wallpaper("bigsur", "Big Sur"),
        new Wallpaper("night-city", "Night City"),
        new Wallpaper("beach", "Beach"),
        new Wallpaper("meadow", "Meadow"),
        new Wallpaper("space", "Deep Space"),
        // Comic / cel-shaded illustrated scenes (all original artwork).
        new Wallpaper("jungle-temple", "Jungle Temple"),
        new Wallpaper("desert-dunes", "Desert Dunes"),
        new Wallpaper("hero-hills", "Hero's Hills"),
        new Wallpaper("spooky-manor", "Spooky Manor"),
        new Wallpaper("toon-town", "Toon Town"),
        new Wallpaper("monster-meadow", "Monster Meadow"),
        // Photo-style scenes (layered gradients) in the same vein as Mountains / Big Sur.
        new Wallpaper("earth", "Earth"),
        new Wallpaper("yosemite", "Yosemite"),
        new Wallpaper("galaxy", "Galaxy"),
        // Photorealistic stock photos (Unsplash, free license), bundled locally.
        new Wallpaper("valley-dawn", "Valley Dawn"),
        new Wallpaper("highlands", "Highlands"),
        new Wallpaper("forest-path", "Forest Path"),
        new Wallpaper("city-avenue", "City Avenue"),
        new Wallpaper("liquid-marble", "Liquid Marble"),
        new Wallpaper("bubble-nebula", "Bubble Nebula"),
    };

    /// <summary>The small "sticky" set always shown up-front in the Settings picker; the rest of
    /// <see cref="All"/> lives behind the "More wallpapers" dialog. A deliberate spread across the
    /// styles (gradient / illustrated / real photo) so the default row already feels varied.</summary>
    public static readonly IReadOnlyList<string> Defaults = new[]
    {
        "aurora", "dusk", "ocean", "mountains", "valley-dawn", "bubble-nebula",
    };

    public static bool IsValid(string key) => All.Any(w => w.Key == key);
    public static string Normalize(string? key) => key is not null && IsValid(key) ? key : "aurora";
}
