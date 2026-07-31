namespace MatOS.Web.Config;

/// <summary>An icon's placement on the desktop (pixels from the top-left of the icon field).</summary>
public class IconPos
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Per-user desktop icon placements. Persisted as desktop-layout.json so each
/// user's arrangement survives restarts. Keyed by user id -> (icon key -> position).</summary>
public class DesktopLayoutStore
{
    public Dictionary<string, Dictionary<string, IconPos>> Users { get; set; } = new();
}

/// <summary>Which app icons each user has pinned to the desktop (keyed by user id -> icon keys).
/// Container/stack apps are only shown on the desktop when pinned.</summary>
public class DesktopPinsStore
{
    public Dictionary<string, List<string>> Users { get; set; } = new();
}
