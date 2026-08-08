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

    /// <summary>Compatibility mode (on by default): when opening an app, route it through the bundled
    /// Caddy with framing headers stripped ("Allow embedding") so every app opens inside a matOS window.
    /// Caddy listens on a dedicated host port per app and the app embeds as http://&lt;access-host&gt;:&lt;port&gt;,
    /// which works DNS-free over a bare hostname/IP (unlike a *.apps.localhost domain). Turn it off to use
    /// the app's own direct host port (no bundled Caddy / no header stripping).</summary>
    public bool CompatibilityMode { get; set; } = true;

    /// <summary>First host port used for compatibility-mode embedding. Caddy publishes the range
    /// [EmbedPortStart, EmbedPortStart+EmbedPortCount); this MUST match the port range published on the
    /// bundled Caddy container in docker-compose.yml.</summary>
    public int EmbedPortStart { get; set; } = 21000;

    /// <summary>How many consecutive host ports are reserved for embedding (see <see cref="EmbedPortStart"/>).</summary>
    public int EmbedPortCount { get; set; } = 50;

    /// <summary>Host-port pool that installed apps publish their UI on. New installs get the next free
    /// port in [PortPoolStart, PortPoolEnd]; the allocator wraps back to the start when it reaches the
    /// end. Change these to e.g. 50000–59999 to move all app ports into a custom range.</summary>
    public int PortPoolStart { get; set; } = 20000;
    public int PortPoolEnd { get; set; } = 65535;
}

/// <summary>Stable host-port assignment per stack for compatibility-mode embedding. Each opened app keeps
/// its port across restarts so Caddy's per-port listener and the embed URL stay consistent. Persisted as
/// embedports.json.</summary>
public class EmbedPortsConfig
{
    public Dictionary<string, int> Ports { get; set; } = new();
}

/// <summary>System-wide list of stacks hidden from the desktop (icon field + start menu). Seeded on
/// first use with matOS's own stack (matOS + Caddy + Matcad); the user can add/remove any stack.</summary>
public class HiddenConfig
{
    public bool Initialized { get; set; }
    public List<string> Stacks { get; set; } = new();
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
